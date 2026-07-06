using System.Security.Cryptography;

namespace Slate.Server.Auth;

/// <summary>Generates URL-safe, high-entropy bearer tokens (refresh tokens, invite links).</summary>
public static class OpaqueTokenGenerator
{
    public static string Generate(int byteLength = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
