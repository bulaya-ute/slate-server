namespace Slate.Server.Storage;

/// <summary>
/// Thrown by <see cref="IVaultStorage.SafePath"/> when a caller-supplied vault-relative path fails
/// validation (traversal, rooted, reserved, or otherwise unsafe to resolve onto disk). Controllers
/// catch this and translate it into a 400 "invalid_path" error envelope.
/// </summary>
public class VaultPathException : Exception
{
    public VaultPathException(string message) : base(message)
    {
    }
}
