using System.Security.Cryptography;
using System.Text;

namespace Slate.Server.Common;

/// <summary>
/// Computes the same SHA-256-lowercase-hex/size pair that <see cref="Storage.IVaultStorage.WriteNoteAtomicAsync"/>
/// and <see cref="Storage.IVaultStorage.WriteAttachmentAtomicAsync"/> return after writing - but
/// before any disk write happens. Callers (NoteService, AttachmentsController) need the hash to
/// build the DB-side row/revision ahead of the disk write (see the dual-write ordering established
/// in Vaults/TreeController.cs: DB transaction -> save -> disk write -> commit), and the hash is a
/// pure function of the content, so pre-computing it here is guaranteed to match what the eventual
/// disk write produces for the same bytes.
/// </summary>
public static class ContentHasher
{
    public static (string Sha256, long SizeBytes) Compute(string content) => Compute(Encoding.UTF8.GetBytes(content));

    public static (string Sha256, long SizeBytes) Compute(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (hash, bytes.LongLength);
    }
}
