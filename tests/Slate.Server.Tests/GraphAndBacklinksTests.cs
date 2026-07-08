using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class GraphAndBacklinksTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public GraphAndBacklinksTests(TestApp app)
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

    private static async Task<JsonElement> CreateNoteAsync(HttpClient client, Guid vaultId, string path, string content)
    {
        var response = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/notes", new { path, content });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Backlinks_ReturnsSourceNotesLinkingToTarget_WithContextSnippet()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "graph-admin-1");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Graph Vault");

        var target = await CreateNoteAsync(client, vaultId, "target.md", "# Target\n\nThe destination note.");
        var targetId = target.GetProperty("id").GetString();

        await CreateNoteAsync(client, vaultId, "linker.md",
            "# Linker\n\nHere is a reference to [[target]] in context, worth noting.");

        var response = await client.GetAsync($"/api/notes/{targetId}/backlinks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var backlinks = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Single(backlinks);
        Assert.Equal("linker.md", backlinks[0].GetProperty("path").GetString());
        Assert.Equal("Linker", backlinks[0].GetProperty("title").GetString());
        var snippet = backlinks[0].GetProperty("contextSnippet").GetString();
        Assert.False(string.IsNullOrWhiteSpace(snippet));
        Assert.Contains("target", snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backlinks_NoteWithNoIncomingLinks_ReturnsEmptyArray()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "graph-admin-2");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var note = await CreateNoteAsync(client, vaultId, "lonely.md", "Nobody links here.");
        var response = await client.GetAsync($"/api/notes/{note.GetProperty("id").GetString()}/backlinks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Graph_ReturnsNodesWithLinkCountsAndResolvedEdges()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "graph-admin-3");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Graph Vault 3");

        var a = await CreateNoteAsync(client, vaultId, "a.md", "# A\n\nLinks to [[b]].");
        var b = await CreateNoteAsync(client, vaultId, "b.md", "# B\n\nLinks to [[c]] and also unresolved [[nowhere]].");
        var c = await CreateNoteAsync(client, vaultId, "c.md", "# C\n\nNo outgoing links.");

        var aId = a.GetProperty("id").GetString();
        var bId = b.GetProperty("id").GetString();
        var cId = c.GetProperty("id").GetString();

        var response = await client.GetAsync($"/api/vaults/{vaultId}/graph");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var graph = await response.Content.ReadFromJsonAsync<JsonElement>();
        var nodes = graph.GetProperty("nodes").EnumerateArray().ToList();
        var edges = graph.GetProperty("edges").EnumerateArray().ToList();

        Assert.Equal(3, nodes.Count);

        // a -> b and b -> c are resolved edges; b -> nowhere is not, so only 2 edges total.
        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.GetProperty("source").GetString() == aId && e.GetProperty("target").GetString() == bId);
        Assert.Contains(edges, e => e.GetProperty("source").GetString() == bId && e.GetProperty("target").GetString() == cId);

        var nodeA = nodes.Single(n => n.GetProperty("id").GetString() == aId);
        var nodeB = nodes.Single(n => n.GetProperty("id").GetString() == bId);
        var nodeC = nodes.Single(n => n.GetProperty("id").GetString() == cId);

        Assert.Equal(1, nodeA.GetProperty("linkCount").GetInt32()); // a->b
        Assert.Equal(2, nodeB.GetProperty("linkCount").GetInt32()); // a->b (incoming) + b->c (outgoing)
        Assert.Equal(1, nodeC.GetProperty("linkCount").GetInt32()); // b->c (incoming)
    }

    [Fact]
    public async Task Graph_EmptyVault_ReturnsEmptyNodesAndEdges()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "graph-admin-4");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Empty Graph Vault");

        var response = await client.GetAsync($"/api/vaults/{vaultId}/graph");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var graph = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(graph.GetProperty("nodes").EnumerateArray());
        Assert.Empty(graph.GetProperty("edges").EnumerateArray());
    }
}
