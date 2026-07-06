using System.Security.Cryptography;
using System.Text;

namespace Slate.Server.Auth;

/// <summary>
/// One-way hash used for opaque, bearer-style secrets stored in the database (refresh tokens,
/// invite tokens): we only ever need to compare presented-token to stored-hash, never recover
/// the original, so plain SHA-256 (not Argon2id - these are already high-entropy random values,
/// not human-guessable passwords) over the raw token bytes is sufficient.
/// </summary>
public static class TokenHasher
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
