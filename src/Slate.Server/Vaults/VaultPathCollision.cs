using Slate.Server.Storage;

namespace Slate.Server.Vaults;

/// <summary>
/// Case-insensitive collision detection against everything currently on disk for a vault (folders,
/// note files, attachments - <see cref="IVaultStorage.ListAll"/> sees them all). Windows/macOS
/// filesystems collapse case (so "Notes" and "notes" are the same directory entry there) but
/// Postgres and Linux prod don't - this catches the mismatch at the app level rather than letting it
/// manifest as a confusing disk-level failure or silent divergence. Originally introduced for folder
/// create/rename (S3); reused as-is for note create/rename (S4) since both write through the same
/// on-disk namespace.
/// </summary>
public static class VaultPathCollision
{
    /// <summary>
    /// True if some existing file or folder already occupies <paramref name="targetPath"/>
    /// case-insensitively under a different exact casing. Entries at or under
    /// <paramref name="excludePrefix"/> are ignored, so renaming something to a different case of
    /// its own current name (or moving its own contents) doesn't flag itself. Only checks the exact
    /// target path, not intermediate ancestor segments, to keep this cheap.
    /// </summary>
    public static bool HasCaseOnlyCollision(IVaultStorage storage, Guid vaultId, string targetPath, string? excludePrefix = null)
    {
        foreach (var entry in storage.ListAll(vaultId))
        {
            if (excludePrefix is not null
                && (entry.Path == excludePrefix || entry.Path.StartsWith(excludePrefix + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (entry.Path != targetPath && entry.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
