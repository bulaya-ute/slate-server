namespace Slate.Server.Storage;

/// <summary>
/// Thrown by <see cref="IVaultStorage"/> write operations (<see cref="IVaultStorage.CreateFolder"/>,
/// <see cref="IVaultStorage.MoveFolder"/>, <see cref="IVaultStorage.MoveAsync"/>) when the
/// destination already exists on disk - a genuine, expected collision that callers should surface
/// as a 409. Deriving from <see cref="IOException"/> keeps it catchable by existing generic IO
/// handling, but callers that need to tell "destination already exists" apart from any other IO
/// failure (permissions, disk full, cross-volume moves, ...) should catch this type first.
/// </summary>
public class VaultConflictException : IOException
{
    public VaultConflictException(string message) : base(message)
    {
    }
}
