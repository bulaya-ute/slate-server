using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class RegisterInviteTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public RegisterInviteTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_WithInvite_HonorsInvitedRole()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { role = "admin" });
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();
        Assert.Equal("admin", invite.GetProperty("role").GetString());

        var anonymousClient = _app.CreateClient();
        var registerResponse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "invited-admin",
            password = "another-strong-password",
            displayName = "Invited Admin",
        });

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        var body = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task Register_WithInvite_DefaultsToUserRole()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { });
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();

        var anonymousClient = _app.CreateClient();
        var registerResponse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "invited-user",
            password = "another-strong-password",
            displayName = "Invited User",
        });

        var body = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user", body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Register_WithAlreadyUsedInvite_IsRejected()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { });
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();

        var anonymousClient = _app.CreateClient();
        var firstUse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "first-user",
            password = "another-strong-password",
            displayName = "First User",
        });
        Assert.Equal(HttpStatusCode.OK, firstUse.StatusCode);

        var secondUse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "second-user",
            password = "another-strong-password",
            displayName = "Second User",
        });

        Assert.Equal(HttpStatusCode.Gone, secondUse.StatusCode);
        var body = await secondUse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invite_already_used", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_WithExpiredInvite_IsRejected()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { expiresInHours = -1 });
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();

        var anonymousClient = _app.CreateClient();
        var registerResponse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "too-late",
            password = "another-strong-password",
            displayName = "Too Late",
        });

        Assert.Equal(HttpStatusCode.Gone, registerResponse.StatusCode);
        var body = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invite_expired", body.GetProperty("error").GetProperty("code").GetString());
    }

    // Reproduces the invite-redemption TOCTOU directly: without the atomic claim, both concurrent
    // requests can observe the invite as unused before either commits, redeeming a single-use
    // invite twice (two user rows from one invite). Uses distinct usernames so the only possible
    // rejection reason for the loser is "invite already used", not a username collision.
    [Fact]
    public async Task Register_WithSameInviteToken_ConcurrentRequests_OnlyOneSucceeds()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { });
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();

        var clientA = _app.CreateClient();
        var clientB = _app.CreateClient();

        var requestA = clientA.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "concurrent-invite-a",
            password = "another-strong-password",
            displayName = "Concurrent A",
        });
        var requestB = clientB.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "concurrent-invite-b",
            password = "another-strong-password",
            displayName = "Concurrent B",
        });

        var responses = await Task.WhenAll(requestA, requestB);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Gone));

        foreach (var goneResponse in responses.Where(r => r.StatusCode == HttpStatusCode.Gone))
        {
            var goneBody = await goneResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invite_already_used", goneBody.GetProperty("error").GetProperty("code").GetString());
        }

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var createdCount = await db.Users.CountAsync(
            u => u.Username == "concurrent-invite-a" || u.Username == "concurrent-invite-b");
        Assert.Equal(1, createdCount);
    }

    [Fact]
    public async Task Register_WithUnknownToken_ReturnsUnauthorized()
    {
        var anonymousClient = _app.CreateClient();
        var registerResponse = await anonymousClient.PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken = "not-a-real-invite",
            username = "nobody",
            password = "another-strong-password",
            displayName = "Nobody",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, registerResponse.StatusCode);
    }
}
