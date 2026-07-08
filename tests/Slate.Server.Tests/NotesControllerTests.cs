using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class NotesControllerTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public NotesControllerTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateVaultAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/vaults", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task CreateNote_ThenGetContent_RoundTripsWithRevIdAndHashHeaders()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-1");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Notes Vault 1");

        var createResponse = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "hello.md", content = "# Hello\n\nWorld." });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();
        Assert.Equal("hello.md", created.GetProperty("path").GetString());
        Assert.Equal("Hello", created.GetProperty("title").GetString());
        Assert.False(created.GetProperty("hasConflict").GetBoolean());
        var headRevId = created.GetProperty("headRevId").GetInt64();
        Assert.True(headRevId > 0);

        var contentResponse = await client.GetAsync($"/api/notes/{noteId}/content");
        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        Assert.Equal("# Hello\n\nWorld.", await contentResponse.Content.ReadAsStringAsync());
        Assert.Equal(headRevId.ToString(), contentResponse.Headers.GetValues("X-Rev-Id").Single());
        Assert.True(contentResponse.Headers.Contains("X-Content-Hash"));

        // File actually landed on disk.
        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "hello.md");
        Assert.True(File.Exists(fullPath));
        Assert.Equal("# Hello\n\nWorld.", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task CreateNote_PathNotEndingInMd_ReturnsBadRequest()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-2");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "hello.txt", content = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_path", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateNote_DuplicatePath_ReturnsConflict()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-3");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "dup.md", content = "one" });
        var second = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "dup.md", content = "two" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("note_exists", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateNote_CaseOnlyCollisionWithExistingNote_ReturnsConflict()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-4");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "Note.md", content = "one" });
        var second = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "note.md", content = "two" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("case_conflict", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateContent_MatchingBaseRevId_SucceedsAndReturnsNewRevIdAndHash()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-5");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var created = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "edit.md", content = "v1" })).Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();
        var headRevId = created.GetProperty("headRevId").GetInt64();

        var updateResponse = await client.PutAsJsonAsync($"/api/notes/{noteId}/content",
            new { content = "v2", baseRevId = headRevId, deviceId = "device-a" });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        var newRevId = updated.GetProperty("revId").GetInt64();
        Assert.True(newRevId > headRevId);
        Assert.False(string.IsNullOrWhiteSpace(updated.GetProperty("contentHash").GetString()));

        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "edit.md");
        Assert.Equal("v2", await File.ReadAllTextAsync(fullPath));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var revision = await db.Revisions.SingleAsync(r => r.Id == newRevId);
        Assert.Equal("device-a", revision.DeviceId);
        Assert.Equal(RevisionKind.Edit, revision.Kind);
        Assert.False(revision.IsConflict);
    }

    [Fact]
    public async Task UpdateContent_StaleBaseRevId_Returns409AndStoresConflictBlobAndLeavesHeadUntouched()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-6");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var created = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "conflict.md", content = "original" })).Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();
        var originalHeadRevId = created.GetProperty("headRevId").GetInt64();

        // A concurrent, successful edit moves the head first.
        var firstEdit = await client.PutAsJsonAsync($"/api/notes/{noteId}/content",
            new { content = "edited-by-someone-else", baseRevId = originalHeadRevId, deviceId = "device-a" });
        Assert.Equal(HttpStatusCode.OK, firstEdit.StatusCode);
        var newHeadRevId = (await firstEdit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revId").GetInt64();

        // A stale client still thinks the head is the original revision.
        var staleEdit = await client.PutAsJsonAsync($"/api/notes/{noteId}/content",
            new { content = "my conflicting edit", baseRevId = originalHeadRevId, deviceId = "device-b" });

        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
        var conflictBody = await staleEdit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newHeadRevId, conflictBody.GetProperty("headRevId").GetInt64());
        var conflictRevId = conflictBody.GetProperty("conflictRevId").GetInt64();
        Assert.True(conflictRevId > newHeadRevId);

        // Head file on disk is untouched by the losing write.
        var fullPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "conflict.md");
        Assert.Equal("edited-by-someone-else", await File.ReadAllTextAsync(fullPath));

        // Conflict blob exists on disk with the losing content.
        var conflictBlobPath = Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), ".slate", "conflicts", $"{conflictRevId}.md");
        Assert.True(File.Exists(conflictBlobPath));
        Assert.Equal("my conflicting edit", await File.ReadAllTextAsync(conflictBlobPath));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var note = await db.Notes.SingleAsync(n => n.Id == Guid.Parse(noteId!));
        Assert.True(note.HasConflict);
        Assert.Equal(newHeadRevId, note.HeadRevId);

        var conflictRevision = await db.Revisions.SingleAsync(r => r.Id == conflictRevId);
        Assert.True(conflictRevision.IsConflict);
        Assert.Equal("device-b", conflictRevision.DeviceId);
    }

    [Fact]
    public async Task RenameNote_MovesFileAndResolvesPreviouslyUnresolvedLinks()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-7");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        // Linking note references a target that doesn't exist yet - unresolved.
        var linker = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "linker.md", content = "See [[Target Note]] for details." })).Content.ReadFromJsonAsync<JsonElement>();
        var linkerId = linker.GetProperty("id").GetString();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            var link = await db.Links.SingleAsync(l => l.SourceNoteId == Guid.Parse(linkerId!));
            Assert.Null(link.TargetNoteId);
        }

        // Create the target under a different name, then rename it to match the link text.
        var target = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "wrong-name.md", content = "I am the target." })).Content.ReadFromJsonAsync<JsonElement>();
        var targetId = target.GetProperty("id").GetString();

        var renameResponse = await client.PostAsJsonAsync($"/api/notes/{targetId}/rename", new { newPath = "Target Note.md" });
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Target Note.md", renamed.GetProperty("path").GetString());

        Assert.False(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "wrong-name.md")));
        Assert.True(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "Target Note.md")));

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            var link = await db.Links.SingleAsync(l => l.SourceNoteId == Guid.Parse(linkerId!));
            Assert.Equal(Guid.Parse(targetId!), link.TargetNoteId);

            var renameRevision = await db.Revisions.SingleAsync(r => r.NoteId == Guid.Parse(targetId!) && r.Kind == RevisionKind.Rename);
            Assert.Equal("wrong-name.md", renameRevision.OldPath);
            Assert.Equal("Target Note.md", renameRevision.Path);
        }
    }

    [Fact]
    public async Task RenameNote_DestinationAlreadyExists_ReturnsConflict()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-8");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "a.md", content = "a" });
        var b = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "b.md", content = "b" })).Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync($"/api/notes/{b.GetProperty("id").GetString()}/rename", new { newPath = "a.md" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteNote_SoftDeletesRowAndRemovesFileAndAppendsDeleteRevision()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-9");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var created = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "gone.md", content = "bye" })).Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();

        var deleteResponse = await client.DeleteAsync($"/api/notes/{noteId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.False(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "gone.md")));

        var getResponse = await client.GetAsync($"/api/notes/{noteId}/content");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var note = await db.Notes.SingleAsync(n => n.Id == Guid.Parse(noteId!));
        Assert.True(note.IsDeleted);
        Assert.True(await db.Revisions.AnyAsync(r => r.NoteId == note.Id && r.Kind == RevisionKind.Delete));
    }

    [Fact]
    public async Task NonMember_CreateNote_Returns404()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-10");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Private Vault");

        await client.PostAsJsonAsync("/api/users", new { username = "notes-outsider-10", password = "another-strong-password", displayName = "Outsider" });
        var outsiderLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "notes-outsider-10", "another-strong-password");
        var outsiderClient = _app.CreateClient();
        outsiderClient.UseBearerToken(outsiderLogin.AccessToken);

        var response = await outsiderClient.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "sneaky.md", content = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMember_CanReadContent_ButCannotUpdate()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "notes-admin-11");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Read Vault");

        var created = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "readable.md", content = "hi" })).Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();
        var headRevId = created.GetProperty("headRevId").GetInt64();

        await client.PostAsJsonAsync("/api/users", new { username = "reader-notes-11", password = "another-strong-password", displayName = "Reader" });
        var readerLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "reader-notes-11", "another-strong-password");
        var readerId = readerLogin.User.GetProperty("id").GetString();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            db.VaultMembers.Add(new VaultMember { VaultId = vaultId, UserId = Guid.Parse(readerId!), Access = VaultAccessLevel.Read });
            await db.SaveChangesAsync();
        }

        var readerClient = _app.CreateClient();
        readerClient.UseBearerToken(readerLogin.AccessToken);

        var readResponse = await readerClient.GetAsync($"/api/notes/{noteId}/content");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);

        var updateResponse = await readerClient.PutAsJsonAsync($"/api/notes/{noteId}/content",
            new { content = "nope", baseRevId = headRevId, deviceId = "reader-device" });
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }
}
