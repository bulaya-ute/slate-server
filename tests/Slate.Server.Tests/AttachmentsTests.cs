using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Tests;

[Collection(SlateTestCollection.Name)]
public class AttachmentsTests : IAsyncLifetime
{
    private readonly TestApp _app;

    public AttachmentsTests(TestApp app)
    {
        _app = app;
    }

    public Task InitializeAsync() => _app.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateVaultAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/vaults", new { name });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName, string contentType, string? folder = null)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        if (folder is not null)
        {
            form.Add(new StringContent(folder), "folder");
        }

        return form;
    }

    [Fact]
    public async Task Upload_ThenGetFile_ServesBytesAndAppendsAttachRevision()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-1");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Attach Vault");

        var bytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4, 5 }; // fake PNG-ish bytes
        using var form = BuildUpload(bytes, "photo.png", "image/png", folder: "images");

        var uploadResponse = await client.PostAsync($"/api/vaults/{vaultId}/attachments", form);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);

        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("images/photo.png", uploaded.GetProperty("path").GetString());
        Assert.Equal(bytes.Length, uploaded.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("image/png", uploaded.GetProperty("mime").GetString());

        var getResponse = await client.GetAsync($"/api/vaults/{vaultId}/files/images/photo.png");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var served = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, served);
        Assert.Equal("image/png", getResponse.Content.Headers.ContentType?.MediaType);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        var attachment = await db.Attachments.SingleAsync(a => a.VaultId == vaultId && a.Path == "images/photo.png");
        Assert.Equal(bytes.Length, attachment.SizeBytes);

        var revision = await db.Revisions.SingleAsync(r => r.VaultId == vaultId && r.Kind == RevisionKind.Attach);
        Assert.Equal("images/photo.png", revision.Path);
        Assert.Null(revision.NoteId);
    }

    [Fact]
    public async Task Upload_NoFile_ReturnsBadRequest()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-2");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        using var form = new MultipartFormDataContent();
        var response = await client.PostAsync($"/api/vaults/{vaultId}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ReuploadSamePath_UpdatesHashAndAppendsAnotherAttachRevision()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-3");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        using (var firstForm = BuildUpload(new byte[] { 1, 2, 3 }, "doc.bin", "application/octet-stream"))
        {
            var first = await client.PostAsync($"/api/vaults/{vaultId}/attachments", firstForm);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using (var secondForm = BuildUpload(new byte[] { 9, 9, 9, 9 }, "doc.bin", "application/octet-stream"))
        {
            var second = await client.PostAsync($"/api/vaults/{vaultId}/attachments", secondForm);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            var body = await second.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(4, body.GetProperty("sizeBytes").GetInt64());
        }

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        Assert.Equal(1, await db.Attachments.CountAsync(a => a.VaultId == vaultId && a.Path == "doc.bin"));
        Assert.Equal(2, await db.Revisions.CountAsync(r => r.VaultId == vaultId && r.Kind == RevisionKind.Attach));
    }

    [Fact]
    public async Task GetFile_WithAccessTokenQueryParam_WorksWithoutAuthorizationHeader()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-4");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        using var form = BuildUpload(new byte[] { 42, 42, 42 }, "img.jpg", "image/jpeg");
        await client.PostAsync($"/api/vaults/{vaultId}/attachments", form);

        var anonymousClient = _app.CreateClient(); // no Authorization header at all
        var response = await anonymousClient.GetAsync($"/api/vaults/{vaultId}/files/img.jpg?access_token={admin.AccessToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 42, 42, 42 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetFile_NoAuth_ReturnsUnauthorized()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-5");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        using var form = BuildUpload(new byte[] { 1 }, "x.bin", "application/octet-stream");
        await client.PostAsync($"/api/vaults/{vaultId}/attachments", form);

        var anonymousClient = _app.CreateClient();
        var response = await anonymousClient.GetAsync($"/api/vaults/{vaultId}/files/x.bin");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFile_MissingFile_Returns404()
    {
        var client = _app.CreateClient();
        var admin = await AuthTestHelpers.SetupAdminAndLoginAsync(client, username: "attach-admin-6");
        client.UseBearerToken(admin.AccessToken);
        var vaultId = await CreateVaultAsync(client, "Vault");

        var response = await client.GetAsync($"/api/vaults/{vaultId}/files/nope.bin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
