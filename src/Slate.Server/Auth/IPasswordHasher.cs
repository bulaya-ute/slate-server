namespace Slate.Server.Auth;

/// <summary>Hashes and verifies user passwords. Argon2id per the design spec.</summary>
public interface IPasswordHasher
{
    /// <summary>Produces a self-describing encoded hash (parameters + salt travel with it).</summary>
    string Hash(string password);

    /// <summary>Verifies <paramref name="password"/> against a hash produced by <see cref="Hash"/>.</summary>
    bool Verify(string password, string encodedHash);
}
