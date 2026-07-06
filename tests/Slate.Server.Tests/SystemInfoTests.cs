using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class SystemInfoTests
{
    private readonly TestApp _app;

    public SystemInfoTests(TestApp app)
    {
        _app = app;
    }

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
}
