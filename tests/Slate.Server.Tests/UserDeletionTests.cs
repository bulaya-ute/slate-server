using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;

namespace Slate.Server.Tests;

/// <summary>
/// Covers the user-deletion FK behaviors fixed as part of the vault storage/CRUD work: deleting a
/// user who still owns a vault must fail gracefully (409), deleting a user who created invites
/// must not FK-crash (their unused invites simply disappear), and deleting a user who *redeemed*
/// an invite must not delete the invite itself (used_by survives via SetNull).
/// </summary>
[Collection(SlateTestCollection.Name)]
public class UserDeletionTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public UserDeletionTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeleteUser_WhoOwnsVault_ReturnsConflict()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "del-admin-1");
        client.UseBearerToken(admin.AccessToken);

        var createUserResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "vault-owner-del-1",
            password = "another-strong-password",
            displayName = "Vault Owner",
        });
        var createdUser = await createUserResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ownerId = createdUser.GetProperty("id").GetString();

        var ownerLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "vault-owner-del-1", "another-strong-password");
        var ownerClient = _app.CreateClient();
        ownerClient.UseBearerToken(ownerLogin.AccessToken);
        await ownerClient.PostAsJsonAsync("/api/vaults", new { name = "Owned Vault" });

        var deleteResponse = await client.DeleteAsync($"/api/users/{ownerId}");

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        var body = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user_owns_vaults", body.GetProperty("error").GetProperty("code").GetString());

        // The user must still exist - the delete was rejected, not silently partial.
        var listResponse = await client.GetAsync("/api/users");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("username").GetString() == "vault-owner-del-1");
    }

    [Fact]
    public async Task DeleteUser_WhoOwnsVault_SucceedsAfterVaultIsDeleted()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "del-admin-2");
        client.UseBearerToken(admin.AccessToken);

        var createUserResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "vault-owner-del-2",
            password = "another-strong-password",
            displayName = "Vault Owner",
        });
        var createdUser = await createUserResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ownerId = createdUser.GetProperty("id").GetString();

        var ownerLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "vault-owner-del-2", "another-strong-password");
        var ownerClient = _app.CreateClient();
        ownerClient.UseBearerToken(ownerLogin.AccessToken);
        var vaultResponse = await ownerClient.PostAsJsonAsync("/api/vaults", new { name = "Owned Vault 2" });
        var vault = await vaultResponse.Content.ReadFromJsonAsync<JsonElement>();
        var vaultId = vault.GetProperty("id").GetString();

        // Admin (owner/admin-only) deletes the vault first...
        var deleteVaultResponse = await client.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteVaultResponse.StatusCode);

        // ...then deleting the now-vault-less user must succeed.
        var deleteUserResponse = await client.DeleteAsync($"/api/users/{ownerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteUserResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_WhoCreatedUnusedInvite_SucceedsAndInviteIsGone()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "del-admin-3");
        client.UseBearerToken(admin.AccessToken);

        var createSecondAdminResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "second-admin-del-3",
            password = "another-strong-password",
            displayName = "Second Admin",
            role = "admin",
        });
        var secondAdmin = await createSecondAdminResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondAdminId = secondAdmin.GetProperty("id").GetString();

        var secondAdminLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "second-admin-del-3", "another-strong-password");
        var secondAdminClient = _app.CreateClient();
        secondAdminClient.UseBearerToken(secondAdminLogin.AccessToken);

        var inviteResponse = await secondAdminClient.PostAsJsonAsync("/api/invites", new { });
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);

        // Previously FK-crashed (invites.created_by was Restrict): deleting the invite's creator
        // must now succeed, cascading the still-unused invite away with them.
        var deleteResponse = await client.DeleteAsync($"/api/users/{secondAdminId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var remainingInvites = await db.Invites.CountAsync(i => i.CreatedBy == Guid.Parse(secondAdminId!));
        Assert.Equal(0, remainingInvites);
    }

    [Fact]
    public async Task DeleteUser_WhoRedeemedInvite_InviteSurvivesWithUsedBySetNull()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "del-admin-4");
        client.UseBearerToken(admin.AccessToken);

        var inviteResponse = await client.PostAsJsonAsync("/api/invites", new { });
        var invite = await inviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteToken = invite.GetProperty("token").GetString();

        var registerResponse = await _app.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            inviteToken,
            username = "redeemer-del-4",
            password = "another-strong-password",
            displayName = "Redeemer",
        });
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        var registered = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var redeemerId = registered.GetProperty("user").GetProperty("id").GetString();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            var inviteRow = await db.Invites.AsNoTracking().SingleAsync();
            Assert.Equal(Guid.Parse(redeemerId!), inviteRow.UsedBy);
        }

        var deleteResponse = await client.DeleteAsync($"/api/users/{redeemerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            var inviteRow = await db.Invites.AsNoTracking().SingleAsync();
            Assert.Null(inviteRow.UsedBy);
            Assert.NotNull(inviteRow.UsedAt);
        }
    }
}
