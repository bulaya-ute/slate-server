using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class InvitesAdminTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public InvitesAdminTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Admin_CreateInvite_ReturnsTokenExpiryAndRole()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var response = await client.PostAsJsonAsync("/api/invites", new { role = "user", expiresInHours = 48 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.Equal("user", body.GetProperty("role").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Admin_CanListAndDeleteInvites()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/api/invites", new { });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        var listResponse = await client.GetAsync("/api/invites");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(list.EnumerateArray());
        var inviteId = list.EnumerateArray().First().GetProperty("id").GetString();

        var deleteResponse = await client.DeleteAsync($"/api/invites/{inviteId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listAfterDelete = await (await client.GetAsync("/api/invites")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(listAfterDelete.EnumerateArray());
    }

    [Fact]
    public async Task NonAdmin_CreateInvite_ReturnsForbidden()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "regular-invitee",
            password = "another-strong-password",
            displayName = "Regular",
        });
        var regularLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "regular-invitee", "another-strong-password");

        var nonAdminClient = _app.CreateClient();
        nonAdminClient.UseBearerToken(regularLogin.AccessToken);

        var response = await nonAdminClient.PostAsJsonAsync("/api/invites", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
