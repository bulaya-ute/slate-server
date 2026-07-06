using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class SetupTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public SetupTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Setup_Login_Me_RoundTrip_Succeeds()
    {
        var client = _app.CreateClient();

        var setupResponse = await client.PostAsJsonAsync("/api/system/setup", new
        {
            username = "founder",
            password = "correct-horse-battery-staple",
            displayName = "Founding Admin",
        });
        Assert.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        var login = await AuthTestHelpers.LoginAsync(client, "founder", "correct-horse-battery-staple");
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.Equal("founder", login.User.GetProperty("username").GetString());
        Assert.Equal("Founding Admin", login.User.GetProperty("displayName").GetString());
        Assert.Equal("admin", login.User.GetProperty("role").GetString());
        Assert.False(login.User.GetProperty("isDisabled").GetBoolean());

        client.UseBearerToken(login.AccessToken);
        var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(login.User.GetProperty("id").GetString(), me.GetProperty("id").GetString());
        Assert.Equal("founder", me.GetProperty("username").GetString());
        Assert.Equal("admin", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Setup_WhenUsersAlreadyExist_ReturnsGone()
    {
        var client = _app.CreateClient();

        var first = await client.PostAsJsonAsync("/api/system/setup", new
        {
            username = "founder",
            password = "correct-horse-battery-staple",
            displayName = "Founding Admin",
        });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/system/setup", new
        {
            username = "someone-else",
            password = "another-strong-password",
            displayName = "Someone Else",
        });

        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("setup_already_completed", body.GetProperty("error").GetProperty("code").GetString());
    }
}
