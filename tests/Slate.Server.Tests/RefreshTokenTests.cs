using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class RefreshTokenTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public RefreshTokenTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Refresh_RotatesToken_AndTheOldTokenIsRejectedOnReuse()
    {
        var client = _app.CreateClient();
        var login = await AuthTestHelpers.SetupAdminAndLoginAsync(client);

        var firstRefreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, firstRefreshResponse.StatusCode);

        var firstRefreshBody = await firstRefreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rotatedAccessToken = firstRefreshBody.GetProperty("accessToken").GetString();
        var rotatedRefreshToken = firstRefreshBody.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rotatedAccessToken));
        Assert.False(string.IsNullOrWhiteSpace(rotatedRefreshToken));
        Assert.NotEqual(login.RefreshToken, rotatedRefreshToken);

        // The rotated token works.
        var secondRefreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotatedRefreshToken });
        Assert.Equal(HttpStatusCode.OK, secondRefreshResponse.StatusCode);
        var secondRefreshBody = await secondRefreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        var latestRefreshToken = secondRefreshBody.GetProperty("refreshToken").GetString();

        // Replaying the ORIGINAL (now-rotated, revoked) token must be rejected...
        var reuseResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
        var reuseBody = await reuseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refresh_token_reused", reuseBody.GetProperty("error").GetProperty("code").GetString());

        // ...and reuse must revoke the whole family: even the latest, otherwise-valid token from
        // the same chain is now rejected too.
        var afterReuseResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = latestRefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuseResponse.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_ReturnsUnauthorized()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_refresh_token", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var client = _app.CreateClient();
        var login = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(login.AccessToken);

        var logoutResponse = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }
}
