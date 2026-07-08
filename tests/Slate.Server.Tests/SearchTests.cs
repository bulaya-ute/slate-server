using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class SearchTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public SearchTests(TestApp app)
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
    public async Task Search_FindsPhraseAndReturnsHighlightedSnippet()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "search-admin-1");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Search Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new
        {
            path = "recipe.md",
            content = "# Grandma's Recipe\n\nThe secret ingredient is roasted garlic and fresh basil, always.",
        });
        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new
        {
            path = "unrelated.md",
            content = "# Unrelated\n\nNothing to see here, just some other topic entirely.",
        });

        var response = await client.GetAsync($"/api/vaults/{vaultId}/search?q=roasted%20garlic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        var array = results.EnumerateArray().ToList();
        Assert.Single(array);

        var hit = array[0];
        Assert.Equal("recipe.md", hit.GetProperty("path").GetString());
        Assert.Equal("Grandma's Recipe", hit.GetProperty("title").GetString());
        var snippet = hit.GetProperty("snippetHtml").GetString();
        Assert.Contains("<mark>", snippet);
        Assert.Contains("</mark>", snippet);
        Assert.True(hit.GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task Search_MissingQuery_ReturnsBadRequest()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "search-admin-2");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.GetAsync($"/api/vaults/{vaultId}/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmptyArray()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "search-admin-3");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path = "a.md", content = "apples and oranges" });

        var response = await client.GetAsync($"/api/vaults/{vaultId}/search?q=xylophone");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(results.EnumerateArray());
    }
}
