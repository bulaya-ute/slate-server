namespace Slate.Server.Storage;

/// <summary>One entry discovered by <see cref="IVaultStorage.ListAll"/>: a file or a directory.</summary>
public record VaultEntry(string Path, bool IsDirectory);

/// <summary>
/// The single gateway for all disk IO under a vault's content directory
/// ({SLATE_DATA_DIR}/vaults/{vaultId}/...). No other component should touch vault files directly -
/// this is what makes path-traversal protection, atomic writes, and (later) watcher echo
/// suppression enforceable in one place.
///
/// Every path accepted or returned here is vault-relative, forward-slash, no leading slash (e.g.
/// "folder/note.md"), never an absolute filesystem path - see <see cref="SafePath"/>.
/// </summary>
public interface IVaultStorage
{
    /// <summary>
    /// Validates a vault-relative path and returns it unchanged if safe. Throws
    /// <see cref="VaultPathException"/> for: null/empty/whitespace, any ".." or "." segment,
    /// a rooted/absolute path (leading slash, drive letter, UNC), any backslash (the wire contract
    /// is forward-slash only - a literal backslash never has a legitimate reason to appear and is
    /// rejected outright rather than silently reinterpreted), empty segments (e.g. "a//b"), a
    /// segment matching a Windows-reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9), a
    /// segment containing characters invalid in a filename on the host OS, or a segment equal to
    /// ".slate" (that folder is reserved for internal conflict-blob storage - valid on disk, never
    /// a valid note/attachment/folder path).
    /// </summary>
    string SafePath(string path);

    /// <summary>Reads a note's full text content. Throws <see cref="FileNotFoundException"/> if it doesn't exist.</summary>
    Task<string> ReadNoteAsync(Guid vaultId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a note's content atomically (temp file in the same directory, then rename over the
    /// destination) so readers never observe a partially-written file. Creates parent directories
    /// as needed. Registers a write-marker for the resulting path/hash (see <see cref="RegisterWrite"/>)
    /// before the rename, for S5's watcher echo suppression. Returns the SHA-256 hash (lowercase hex)
    /// and byte length of the UTF-8-encoded content.
    /// </summary>
    Task<(string Sha256, long SizeBytes)> WriteNoteAtomicAsync(Guid vaultId, string path, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an attachment's raw bytes atomically (same temp-file-then-rename discipline as
    /// <see cref="WriteNoteAtomicAsync"/>, just without the UTF-8 text encoding step - attachments
    /// are arbitrary binary content). Returns the SHA-256 hash (lowercase hex) and byte length.
    /// </summary>
    Task<(string Sha256, long SizeBytes)> WriteAttachmentAtomicAsync(Guid vaultId, string path, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Reads an attachment's raw bytes. Throws <see cref="FileNotFoundException"/> if it doesn't exist.</summary>
    Task<byte[]> ReadAttachmentAsync(Guid vaultId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a sync-conflict blob under the vault's reserved <c>.slate/conflicts/</c> subtree,
    /// keyed by the revision id that recorded the conflict (see the Sync protocol conflict path in
    /// the design spec). Deliberately bypasses <see cref="SafePath"/>'s "'.slate' is reserved"
    /// rejection - this is the one legitimate internal writer of that subtree, never reachable from
    /// caller-supplied note/attachment/folder paths. Returns the SHA-256 hash (lowercase hex) and
    /// byte length of the UTF-8-encoded content.
    /// </summary>
    Task<(string Sha256, long SizeBytes)> WriteConflictBlobAsync(Guid vaultId, long revisionId, string content, CancellationToken cancellationToken = default);

    /// <summary>Reads a previously written conflict blob. Throws <see cref="FileNotFoundException"/> if it doesn't exist.</summary>
    Task<string> ReadConflictBlobAsync(Guid vaultId, long revisionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a single file. Idempotent: no error if it's already gone.</summary>
    Task DeleteAsync(Guid vaultId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves/renames a single file. Throws <see cref="FileNotFoundException"/> if the source is
    /// missing, or <see cref="VaultConflictException"/> if the destination already exists.
    /// </summary>
    Task MoveAsync(Guid vaultId, string fromPath, string toPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recursively lists every file and directory under the vault's root, as vault-relative
    /// forward-slash paths, excluding the reserved .slate subtree. Returns an empty list if the
    /// vault root doesn't exist on disk yet.
    /// </summary>
    IReadOnlyList<VaultEntry> ListAll(Guid vaultId);

    /// <summary>
    /// Creates a folder (and any missing parents). Idempotent: no error if it already exists.
    /// Throws <see cref="VaultConflictException"/> if a file (not a directory) already occupies
    /// that exact path.
    /// </summary>
    void CreateFolder(Guid vaultId, string path);

    /// <summary>True if a directory exists at the given vault-relative path.</summary>
    bool FolderExists(Guid vaultId, string path);

    /// <summary>Recursively deletes a folder and everything under it. Idempotent: no error if it's already gone.</summary>
    void DeleteFolder(Guid vaultId, string path);

    /// <summary>
    /// Moves/renames a folder (and everything under it) in one filesystem operation. Throws
    /// <see cref="DirectoryNotFoundException"/> if the source is missing, or
    /// <see cref="VaultConflictException"/> if the destination already exists.
    /// </summary>
    void MoveFolder(Guid vaultId, string fromPath, string toPath);

    /// <summary>Ensures the vault's root content directory exists on disk (called on vault creation).</summary>
    void EnsureVaultRoot(Guid vaultId);

    /// <summary>Recursively deletes the vault's entire content directory (called on vault deletion).</summary>
    void DeleteVaultRoot(Guid vaultId);

    /// <summary>
    /// Records that this process just wrote <paramref name="hash"/> to the file at
    /// <paramref name="path"/> (an absolute disk path). For S5's <c>FileSystemWatcher</c>: a
    /// change event that matches a fresh marker is our own write echoing back, not a genuine
    /// external edit, and should be dropped rather than replayed as a filesystem-authored revision.
    /// Markers expire after a short window so a later external write reusing the same content
    /// (e.g. someone puts back identical text) isn't mistaken for an echo.
    /// </summary>
    void RegisterWrite(string path, string hash);

    /// <summary>
    /// True if <paramref name="hash"/> matches a fresh marker previously registered for
    /// <paramref name="path"/> (an absolute disk path) via <see cref="RegisterWrite"/>. Consumes
    /// the marker on a match (one-shot), so a subsequent genuine external write to the same path
    /// isn't accidentally suppressed too.
    /// </summary>
    bool WasOurWrite(string path, string hash);
}
