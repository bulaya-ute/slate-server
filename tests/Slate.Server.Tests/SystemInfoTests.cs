using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class SystemInfoTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public SystemInfoTests(TestApp app)
    {
        _app = app;
    }

    // The TestApp's Postgres container is shared for the whole test session (see TestApp), so
    // without an explicit reset, "fresh, migrated database" would only hold true if this happens
    // to be the very first test that runs - reset before each test method for real isolation.
    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetSystemInfo_ReturnsApiVersion1AndSetupRequiredTrueOnFreshDatabase()
    {
        var client = _app.CreateClient();

        var response = await client.GetAsync("/api/system/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Slate", body.GetProperty("name").GetString());
        Assert.Equal(1, body.GetProperty("apiVersion").GetInt32());
        Assert.Equal("Slate Test Server", body.GetProperty("serverName").GetString());
        // No users have been created in this fresh, migrated database, so setup is required.
        Assert.True(body.GetProperty("setupRequired").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task GetSystemInfo_ReturnsSetupRequiredFalse_OnceAUserExists()
    {
        var client = _app.CreateClient();

        var setupResponse = await client.PostAsJsonAsync("/api/system/setup", new
        {
            username = "info-admin",
            password = "correct-horse-battery-staple",
            displayName = "Info Admin",
        });
        Assert.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        var response = await client.GetAsync("/api/system/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("setupRequired").GetBoolean());
    }
}
