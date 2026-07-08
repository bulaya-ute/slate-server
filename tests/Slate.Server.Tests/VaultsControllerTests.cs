using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class VaultsControllerTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public VaultsControllerTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> CreateVaultAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/vaults", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_ReturnsVaultWithOwnerRole()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-1");
        client.UseBearerToken(admin.AccessToken);

        var vault = await CreateVaultAsync(client, "My Vault");

        Assert.Equal("My Vault", vault.GetProperty("name").GetString());
        Assert.Equal("owner", vault.GetProperty("role").GetString());
        Assert.Equal(0, vault.GetProperty("noteCount").GetInt32());
        Assert.Equal(0, vault.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task GetAll_OnlyListsCallersOwnVaults()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-2");
        client.UseBearerToken(admin.AccessToken);

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "vault-user-2",
            password = "another-strong-password",
            displayName = "Vault User 2",
        });
        var userLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "vault-user-2", "another-strong-password");
        var userClient = _app.CreateClient();
        userClient.UseBearerToken(userLogin.AccessToken);

        await CreateVaultAsync(client, "Admin's Vault");
        await CreateVaultAsync(userClient, "User's Vault");

        var adminList = await (await client.GetAsync("/api/vaults")).Content.ReadFromJsonAsync<JsonElement>();
        var userList = await (await userClient.GetAsync("/api/vaults")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single(adminList.EnumerateArray());
        Assert.Equal("Admin's Vault", adminList[0].GetProperty("name").GetString());

        Assert.Single(userList.EnumerateArray());
        Assert.Equal("User's Vault", userList[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Owner_CanRenameAndDeleteVault()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-3");
        client.UseBearerToken(admin.AccessToken);

        var vault = await CreateVaultAsync(client, "Original Name");
        var vaultId = vault.GetProperty("id").GetString();

        var renameResponse = await client.PatchAsJsonAsync($"/api/vaults/{vaultId}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());

        var deleteResponse = await client.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listAfterDelete = await (await client.GetAsync("/api/vaults")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(listAfterDelete.EnumerateArray());
    }

    [Fact]
    public async Task Delete_RemovesVaultContentDirectoryFromDisk()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-4");
        client.UseBearerToken(admin.AccessToken);

        var vault = await CreateVaultAsync(client, "Disk Vault");
        var vaultId = Guid.Parse(vault.GetProperty("id").GetString()!);

        var vaultDir = Path.Combine(_app.DataDir, "vaults", vaultId.ToString());
        Assert.True(Directory.Exists(vaultDir));

        var deleteResponse = await client.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.False(Directory.Exists(vaultDir));
    }

    [Fact]
    public async Task EditMember_CannotRenameOrDeleteVault()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-5");
        client.UseBearerToken(admin.AccessToken);

        var vault = await CreateVaultAsync(client, "Shared Vault");
        var vaultId = Guid.Parse(vault.GetProperty("id").GetString()!);

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "edit-member-5",
            password = "another-strong-password",
            displayName = "Edit Member",
        });
        var editorLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "edit-member-5", "another-strong-password");
        var editorId = editorLogin.User.GetProperty("id").GetString();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
            db.VaultMembers.Add(new VaultMember
            {
                VaultId = vaultId,
                UserId = Guid.Parse(editorId!),
                Access = VaultAccessLevel.Edit,
            });
            await db.SaveChangesAsync();
        }

        var editorClient = _app.CreateClient();
        editorClient.UseBearerToken(editorLogin.AccessToken);

        var renameResponse = await editorClient.PatchAsJsonAsync($"/api/vaults/{vaultId}", new { name = "Hijacked" });
        Assert.Equal(HttpStatusCode.Forbidden, renameResponse.StatusCode);

        var deleteResponse = await editorClient.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task NonMember_RenameOrDeleteVault_Returns404NotForbidden()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-6");
        client.UseBearerToken(admin.AccessToken);

        var vault = await CreateVaultAsync(client, "Private Vault");
        var vaultId = vault.GetProperty("id").GetString();

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "outsider-6",
            password = "another-strong-password",
            displayName = "Outsider",
        });
        var outsiderLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "outsider-6", "another-strong-password");
        var outsiderClient = _app.CreateClient();
        outsiderClient.UseBearerToken(outsiderLogin.AccessToken);

        var renameResponse = await outsiderClient.PatchAsJsonAsync($"/api/vaults/{vaultId}", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.NotFound, renameResponse.StatusCode);
        var body = await renameResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("error").GetProperty("code").GetString());

        var deleteResponse = await outsiderClient.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_CanDeleteVault_EvenWhenNotAMember()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "vault-admin-7");
        client.UseBearerToken(admin.AccessToken);

        await client.PostAsJsonAsync("/api/users", new
        {
            username = "vault-owner-7",
            password = "another-strong-password",
            displayName = "Vault Owner",
        });
        var ownerLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "vault-owner-7", "another-strong-password");
        var ownerClient = _app.CreateClient();
        ownerClient.UseBearerToken(ownerLogin.AccessToken);

        var vault = await CreateVaultAsync(ownerClient, "Someone Else's Vault");
        var vaultId = vault.GetProperty("id").GetString();

        // The admin is a global Admin but was never added as a vault_members row for this vault.
        var deleteResponse = await client.DeleteAsync($"/api/vaults/{vaultId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_CreateVault_ReturnsUnauthorized()
    {
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/vaults", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
