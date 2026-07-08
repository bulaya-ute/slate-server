using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Storage;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class TreeAndFoldersTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public TreeAndFoldersTests(TestApp app)
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

    /// <summary>
    /// Seeds a note row + its on-disk file directly (Notes controller/create endpoint don't exist
    /// until a later task) so folder rename/delete can be exercised against real note rows.
    /// </summary>
    private async Task<Note> SeedNoteAsync(Guid vaultId, string path, string content = "hello", Guid? authorId = null)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IVaultStorage>();

        var (hash, size) = await storage.WriteNoteAtomicAsync(vaultId, path, content);

        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            Path = path,
            Title = Path.GetFileNameWithoutExtension(path),
            ContentHash = hash,
            SizeBytes = size,
            HasConflict = false,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Notes.Add(note);

        // HeadRevId is nullable specifically because a Note row must exist before its first
        // Revision row can reference it (revisions.note_id -> notes.id) - see Note.cs. Inserting
        // both in one SaveChanges with the Note also pointing at the not-yet-persisted Revision
        // is a genuine circular FK dependency, so this saves in two steps instead.
        await db.SaveChangesAsync();

        if (authorId is not null)
        {
            var initialRevision = new Revision
            {
                VaultId = vaultId,
                NoteId = note.Id,
                AuthorId = authorId,
                DeviceId = "seed",
                Kind = RevisionKind.Create,
                Path = path,
                ContentHash = hash,
                IsConflict = false,
                CreatedAt = now,
            };
            db.Revisions.Add(initialRevision);
            await db.SaveChangesAsync();

            note.HeadRevId = initialRevision.Id;
            await db.SaveChangesAsync();
        }

        return note;
    }

    [Fact]
    public async Task Tree_ForFreshVault_ReturnsEmptyArrays()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-1");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Fresh Vault");

        var response = await client.GetAsync($"/api/vaults/{vaultId}/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("folders").EnumerateArray());
        Assert.Empty(body.GetProperty("notes").EnumerateArray());
    }

    [Fact]
    public async Task CreateFolder_ThenTree_ShowsNestedFolders()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-2");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Folder Vault");

        var createResponse = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/folders", new { path = "a/b/c" });
        Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);

        var tree = await (await client.GetAsync($"/api/vaults/{vaultId}/tree")).Content.ReadFromJsonAsync<JsonElement>();
        var folders = tree.GetProperty("folders").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("a", folders);
        Assert.Contains("a/b", folders);
        Assert.Contains("a/b/c", folders);
    }

    [Fact]
    public async Task CreateFolder_WithTraversalPath_ReturnsBadRequest()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-3");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/folders", new { path = "../escape" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_path", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RenameFolder_MovesFilesAndUpdatesNotePathsAndAppendsRenameRevisions()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-4");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Rename Vault");
        var adminId = Guid.Parse(admin.User.GetProperty("id").GetString()!);

        var noteA = await SeedNoteAsync(vaultId, "docs/a.md", "content A", adminId);
        var noteB = await SeedNoteAsync(vaultId, "docs/sub/b.md", "content B", adminId);

        client.DefaultRequestHeaders.Add("X-Device-Id", "device-xyz");
        var renameResponse = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/folders/rename",
            new { path = "docs", newPath = "documents" });
        Assert.Equal(HttpStatusCode.NoContent, renameResponse.StatusCode);

        // Files moved on disk.
        Assert.False(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "docs", "a.md")));
        Assert.True(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "documents", "a.md")));
        Assert.True(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "documents", "sub", "b.md")));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();

        var updatedA = await db.Notes.SingleAsync(n => n.Id == noteA.Id);
        var updatedB = await db.Notes.SingleAsync(n => n.Id == noteB.Id);
        Assert.Equal("documents/a.md", updatedA.Path);
        Assert.Equal("documents/sub/b.md", updatedB.Path);

        var revisionA = await db.Revisions.SingleAsync(r => r.NoteId == noteA.Id && r.Kind == RevisionKind.Rename);
        Assert.Equal("documents/a.md", revisionA.Path);
        Assert.Equal("docs/a.md", revisionA.OldPath);
        Assert.Equal(adminId, revisionA.AuthorId);
        Assert.Equal("device-xyz", revisionA.DeviceId);
        Assert.Equal(noteA.ContentHash, revisionA.ContentHash);

        var revisionB = await db.Revisions.SingleAsync(r => r.NoteId == noteB.Id && r.Kind == RevisionKind.Rename);
        Assert.Equal("documents/sub/b.md", revisionB.Path);
        Assert.Equal("docs/sub/b.md", revisionB.OldPath);

        // Head revision now points at the rename.
        Assert.Equal(revisionA.Id, updatedA.HeadRevId);
        Assert.Equal(revisionB.Id, updatedB.HeadRevId);
    }

    [Fact]
    public async Task RenameFolder_DeviceIdHeaderMissing_DefaultsToUnknown()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-5");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "No Header Vault");
        var adminId = Guid.Parse(admin.User.GetProperty("id").GetString()!);
        var note = await SeedNoteAsync(vaultId, "old/note.md", "hi", adminId);

        var response = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/folders/rename",
            new { path = "old", newPath = "new" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var revision = await db.Revisions.SingleAsync(r => r.NoteId == note.Id && r.Kind == RevisionKind.Rename);
        Assert.Equal("unknown", revision.DeviceId);
    }

    [Fact]
    public async Task RenameFolder_NonExistentFolder_Returns404()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-6");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/folders/rename",
            new { path = "ghost", newPath = "new-ghost" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("folder_not_found", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeleteFolder_RemovesFilesAndSoftDeletesNotesAndAppendsDeleteRevisions()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-7");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Delete Vault");
        var adminId = Guid.Parse(admin.User.GetProperty("id").GetString()!);
        var note = await SeedNoteAsync(vaultId, "trash/x.md", "gone soon", adminId);

        var response = await client.DeleteAsync($"/api/vaults/{vaultId}/folders?path=trash");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.False(File.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "trash", "x.md")));
        Assert.False(Directory.Exists(Path.Combine(_app.DataDir, "vaults", vaultId.ToString(), "trash")));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var updatedNote = await db.Notes.SingleAsync(n => n.Id == note.Id);
        Assert.True(updatedNote.IsDeleted);

        var deleteRevision = await db.Revisions.SingleAsync(r => r.NoteId == note.Id && r.Kind == RevisionKind.Delete);
        Assert.Equal("trash/x.md", deleteRevision.Path);
        Assert.Equal(adminId, deleteRevision.AuthorId);

        // The tree no longer reports the deleted note or its folder.
        var tree = await (await client.GetAsync($"/api/vaults/{vaultId}/tree")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(tree.GetProperty("notes").EnumerateArray());
        Assert.DoesNotContain(tree.GetProperty("folders").EnumerateArray(), e => e.GetString() == "trash");
    }

    [Fact]
    public async Task DeleteFolder_NonExistentFolder_Returns404()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-8");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.DeleteAsync($"/api/vaults/{vaultId}/folders?path=ghost");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NonMember_GetTree_Returns404NotForbidden()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-9");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Private Tree Vault");

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "tree-outsider-9",
            password = "another-strong-password",
            displayName = "Outsider",
        });
        var outsiderLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "tree-outsider-9", "another-strong-password");
        var outsiderClient = _app.CreateClient();
        outsiderClient.UseBearerToken(outsiderLogin.AccessToken);

        var response = await outsiderClient.GetAsync($"/api/vaults/{vaultId}/tree");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NonMember_CreateFolder_Returns404()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-10");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Private Vault 10");

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "tree-outsider-10",
            password = "another-strong-password",
            displayName = "Outsider",
        });
        var outsiderLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "tree-outsider-10", "another-strong-password");
        var outsiderClient = _app.CreateClient();
        outsiderClient.UseBearerToken(outsiderLogin.AccessToken);

        var response = await outsiderClient.PostAsJsonAsync($"/api/vaults/{vaultId}/folders", new { path = "sneaky" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMember_CanReadTree_ButCannotCreateFolder()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tree-admin-11");
        client.UseBearerToken(admin.AccessToken);

        var vaultId = await CreateVaultAsync(client, "Read Vault 11");

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "reader-11",
            password = "another-strong-password",
            displayName = "Reader",
        });
        var readerLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "reader-11", "another-strong-password");
        var readerId = readerLogin.User.GetProperty("id").GetString();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            db.VaultMembers.Add(new VaultMember
            {
                VaultId = vaultId,
                UserId = Guid.Parse(readerId!),
                Access = VaultAccessLevel.Read,
            });
            await db.SaveChangesAsync();
        }

        var readerClient = _app.CreateClient();
        readerClient.UseBearerToken(readerLogin.AccessToken);

        var treeResponse = await readerClient.GetAsync($"/api/vaults/{vaultId}/tree");
        Assert.Equal(HttpStatusCode.OK, treeResponse.StatusCode);

        var folderResponse = await readerClient.PostAsJsonAsync($"/api/vaults/{vaultId}/folders", new { path = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, folderResponse.StatusCode);
    }
}
