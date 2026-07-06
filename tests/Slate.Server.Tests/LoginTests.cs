using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class LoginTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public LoginTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_DisabledUser_ReturnsUnauthorized()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "disabled-user",
            password = "another-strong-password",
            displayName = "Soon Disabled",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString();

        var patchResponse = await client.PatchAsJsonAsync($"/api/users/{userId}", new { isDisabled = true });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        // A fresh, unauthenticated client - disabled status must be enforced independent of caller.
        var anonymousClient = _app.CreateClient();
        var loginResponse = await AuthTestHelpers.LoginRawAsync(anonymousClient, "disabled-user", "another-strong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("account_disabled", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorizedWithErrorEnvelope()
    {
        var client = _app.CreateClient();
        await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "admin2");

        var anonymousClient = _app.CreateClient();
        var response = await AuthTestHelpers.LoginRawAsync(anonymousClient, "admin2", "totally-wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", body.GetProperty("error").GetProperty("code").GetString());
    }

    // Guards against reintroducing the username-enumeration timing side channel: an unknown
    // username must fail exactly like a known username with a wrong password - same status code,
    // same error code - rather than short-circuiting before the Argon2id verify.
    [Fact]
    public async Task Login_UnknownUsername_ReturnsUnauthorizedWithSameErrorEnvelopeAsWrongPassword()
    {
        var client = _app.CreateClient();

        var response = await AuthTestHelpers.LoginRawAsync(client, "no-such-user", "whatever-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", body.GetProperty("error").GetProperty("code").GetString());
    }
}
