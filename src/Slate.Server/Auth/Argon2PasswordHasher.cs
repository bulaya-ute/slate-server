using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Slate.Server.Auth;

/// <summary>
/// Argon2id password hashing via Konscious.Security.Cryptography, per the design spec.
///
/// Encoded format: "m={memoryKb},t={iterations},p={parallelism}${saltBase64}${hashBase64}" -
/// parameters travel with the hash so they can be tuned later without invalidating existing
/// hashes (a fixed set of constants baked only into code would break verification for anyone
/// hashed under older constants).
/// </summary>
public class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    // OWASP-recommended minimum for Argon2id (interactive login): m=19 MiB, t=2, p=1.
    private const int MemorySizeKb = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, MemorySizeKb, Iterations, Parallelism);
        return $"m={MemorySizeKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        var segments = encodedHash.Split('$');
        if (segments.Length != 3)
        {
            return false;
        }

        try
        {
            var (memoryKb, iterations, parallelism) = ParseParameters(segments[0]);
            var salt = Convert.FromBase64String(segments[1]);
            var expected = Convert.FromBase64String(segments[2]);

            var actual = ComputeHash(password, salt, memoryKb, iterations, parallelism);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKb,
            Iterations = iterations,
        };
        return argon2.GetBytes(HashSizeBytes);
    }

    private static (int MemoryKb, int Iterations, int Parallelism) ParseParameters(string spec)
    {
        var values = spec.Split(',')
            .Select(part => part.Split('='))
            .ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));

        return (values["m"], values["t"], values["p"]);
    }
}
