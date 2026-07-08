using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class TagsControllerTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public TagsControllerTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateVaultAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/vaults", new { name });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task GetTags_ReturnsNamesWithCounts()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tags-admin-1");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Tags Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "a.md", content = "About #project stuff." });
        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "b.md", content = "More #project and #urgent." });
        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "c.md", content = "Just #urgent alone." });

        var response = await client.GetAsync($"/api/vaults/{vaultId}/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tags = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var project = tags.Single(t => t.GetProperty("name").GetString() == "project");
        var urgent = tags.Single(t => t.GetProperty("name").GetString() == "urgent");

        Assert.Equal(2, project.GetProperty("count").GetInt32());
        Assert.Equal(2, urgent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetNotesForTag_ReturnsOnlyNotesWithThatTag()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tags-admin-2");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "tagged.md", content = "Has #special tag." });
        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "untagged.md", content = "No tags here." });

        var response = await client.GetAsync($"/api/vaults/{vaultId}/tags/special/notes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var notes = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Single(notes);
        Assert.Equal("tagged.md", notes[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task GetTags_DeletedNoteTags_AreExcludedFromCounts()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "tags-admin-3");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var created = await (await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes",
            new { path = "solo.md", content = "Only #onlyme here." })).Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetString();

        await client.DeleteAsync($"/api/notes/{noteId}");

        var response = await client.GetAsync($"/api/vaults/{vaultId}/tags");
        var tags = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.DoesNotContain(tags, t => t.GetProperty("name").GetString() == "onlyme");
    }
}
