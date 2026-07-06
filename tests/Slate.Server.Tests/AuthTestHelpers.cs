using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Slate.Server.Tests;

/// <summary>Shared setup/login helpers for auth-dependent integration tests.</summary>
internal static class AuthTestHelpers
{
    public const string DefaultPassword = "correct-horse-battery-staple";

    public static async Task<LoginResult> SetupAdminAndLoginAsync(
        HttpClient client,
        string username = "admin",
        string password = DefaultPassword,
        string displayName = "Admin")
    {
        var setupResponse = await client.PostAsJsonAsync("/api/system/setup", new { username, password, displayName });
        setupResponse.EnsureSuccessStatusCode();

        return await LoginAsync(client, username, password);
    }

    public static async Task<HttpResponseMessage> LoginRawAsync(HttpClient client, string username, string password) =>
        await client.PostAsJsonAsync("/api/auth/login", new { username, password });

    public static async Task<LoginResult> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await LoginRawAsync(client, username, password);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new LoginResult(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("user"));
    }

    public static void UseBearerToken(this HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}

internal record LoginResult(string AccessToken, string RefreshToken, JsonElement User);
