using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class UsersAdminTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public UsersAdminTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task NonAdmin_GetUsers_ReturnsForbidden()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "regular",
            password = "another-strong-password",
            displayName = "Regular User",
            role = "user",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var regularLogin = await AuthTestHelpers.LoginAsync(_app.CreateClient(), "regular", "another-strong-password");

        var nonAdminClient = _app.CreateClient();
        nonAdminClient.UseBearerToken(regularLogin.AccessToken);

        var response = await nonAdminClient.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unauthenticated_GetUsers_ReturnsUnauthorized()
    {
        var client = _app.CreateClient();
        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_CanCreateListUpdateAndDeleteUsers()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client);
        client.UseBearerToken(admin.AccessToken);

        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "crud-user",
            password = "another-strong-password",
            displayName = "CRUD User",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = created.GetProperty("id").GetString();
        Assert.Equal("user", created.GetProperty("role").GetString());

        var listResponse = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("username").GetString() == "crud-user");

        var patchResponse = await client.PatchAsJsonAsync($"/api/users/{userId}", new
        {
            role = "admin",
            isDisabled = true,
            newPassword = "brand-new-strong-password",
        });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patched = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", patched.GetProperty("role").GetString());
        Assert.True(patched.GetProperty("isDisabled").GetBoolean());

        var deleteResponse = await client.DeleteAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listAfterDelete = await (await client.GetAsync("/api/users")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(listAfterDelete.EnumerateArray(), u => u.GetProperty("username").GetString() == "crud-user");
    }

    [Fact]
    public async Task Admin_CreateUser_WithDuplicateUsername_ReturnsConflict()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "dup-admin");
        client.UseBearerToken(admin.AccessToken);

        var first = await client.PostAsJsonAsync("/api/users", new
        {
            username = "dup-admin",
            password = "another-strong-password",
            displayName = "Duplicate",
        });

        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("username_taken", body.GetProperty("error").GetProperty("code").GetString());
    }
}
