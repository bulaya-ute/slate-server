using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Slate.Server.Configuration;

namespace Slate.Server.Storage;

/// <inheritdoc cref="IVaultStorage" />
public class VaultStorage : IVaultStorage
{
    /// <summary>
    /// Reserved segment characters, hardcoded rather than sourced from
    /// <see cref="Path.GetInvalidFileNameChars"/>: that API is host-OS-dependent (near-empty on
    /// Linux, where this app is typically deployed via Docker), and validation must reject the
    /// same paths regardless of which OS the server happens to run on. This is the Windows-NTFS
    /// reserved set - the strictest common denominator - plus the control characters.
    /// </summary>
    private static readonly char[] InvalidSegmentChars = BuildInvalidSegmentChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly TimeSpan MarkerTtl = TimeSpan.FromSeconds(10);

    private readonly string _vaultsRoot;
    private readonly ConcurrentDictionary<string, (string Hash, DateTimeOffset RegisteredAt)> _writeMarkers = new();

    public VaultStorage(SlateOptions options)
    {
        _vaultsRoot = Path.Combine(options.DataDir, "vaults");
    }

    public string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new VaultPathException("Path must not be empty.");
        }

        // The wire contract is forward-slash only (see IVaultStorage docs); a literal backslash
        // is rejected outright rather than reinterpreted as a separator, so a client that leaks a
        // Windows-style path (or an attacker probing "C:\x" / "a\..\..\x") can't smuggle traversal
        // past a split that only looks for '/'.
        if (path.Contains('\\'))
        {
            throw new VaultPathException("Path must use forward slashes, not backslashes.");
        }

        if (path.StartsWith('/') || Path.IsPathRooted(path))
        {
            throw new VaultPathException("Path must be vault-relative, not rooted.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw new VaultPathException("Path must not contain empty segments.");
            }

            if (segment is "." or "..")
            {
                throw new VaultPathException("Path must not contain '.' or '..' segments.");
            }

            if (segment.IndexOfAny(InvalidSegmentChars) >= 0)
            {
                throw new VaultPathException($"Path segment '{segment}' contains invalid characters.");
            }

            // Windows silently strips trailing dots/spaces from a segment when it hits disk (e.g.
            // "notes." and "notes" collide there, and "notes " round-trips as "notes"), so a path
            // that's valid-looking here could resolve to a different, colliding path on that OS.
            // Rejected outright rather than allowed to fail confusingly later.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new VaultPathException($"Path segment '{segment}' must not end with a period or a space.");
            }

            if (segment.Equals(".slate", StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultPathException("The '.slate' folder is reserved.");
            }

            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                throw new VaultPathException($"'{segment}' is a reserved name.");
            }
        }

        return path;
    }

    public async Task<string> ReadNoteAsync(Guid vaultId, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(vaultId, path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No such note: {path}", fullPath);
        }

        return await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
    }

    public Task<(string Sha256, long SizeBytes)> WriteNoteAtomicAsync(
        Guid vaultId, string path, string content, CancellationToken cancellationToken = default, string? precomputedHash = null)
    {
        var fullPath = ResolvePath(vaultId, path);
        return WriteBytesAtomicAsync(fullPath, Encoding.UTF8.GetBytes(content), cancellationToken, precomputedHash);
    }

    public Task<(string Sha256, long SizeBytes)> WriteAttachmentAtomicAsync(
        Guid vaultId, string path, byte[] content, CancellationToken cancellationToken = default, string? precomputedHash = null)
    {
        var fullPath = ResolvePath(vaultId, path);
        return WriteBytesAtomicAsync(fullPath, content, cancellationToken, precomputedHash);
    }

    public async Task<byte[]> ReadAttachmentAsync(Guid vaultId, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(vaultId, path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No such file: {path}", fullPath);
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken);
    }

    public Task<(string Sha256, long SizeBytes)> WriteConflictBlobAsync(
        Guid vaultId, long revisionId, string content, CancellationToken cancellationToken = default, string? precomputedHash = null)
    {
        // Bypasses ResolvePath/SafePath entirely - revisionId is a server-generated bigserial, never
        // caller-supplied text, so there's no traversal surface to validate against here, and
        // SafePath would reject the ".slate" segment outright regardless (see IVaultStorage docs).
        var fullPath = ConflictBlobPath(vaultId, revisionId);
        return WriteBytesAtomicAsync(fullPath, Encoding.UTF8.GetBytes(content), cancellationToken, precomputedHash);
    }

    public async Task<string> ReadConflictBlobAsync(Guid vaultId, long revisionId, CancellationToken cancellationToken = default)
    {
        var fullPath = ConflictBlobPath(vaultId, revisionId);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No such conflict blob: {revisionId}", fullPath);
        }

        return await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
    }

    public Task DeleteAsync(Guid vaultId, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(vaultId, path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(Guid vaultId, string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        var fullFrom = ResolvePath(vaultId, fromPath);
        var fullTo = ResolvePath(vaultId, toPath);

        if (!File.Exists(fullFrom))
        {
            throw new FileNotFoundException($"No such note: {fromPath}", fullFrom);
        }

        if (File.Exists(fullTo))
        {
            throw new VaultConflictException($"A file already exists at '{toPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
        File.Move(fullFrom, fullTo);

        return Task.CompletedTask;
    }

    public IReadOnlyList<VaultEntry> ListAll(Guid vaultId)
    {
        var root = VaultRoot(vaultId);
        var results = new List<VaultEntry>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        Walk(root, string.Empty, results);
        return results;
    }

    public void CreateFolder(Guid vaultId, string path)
    {
        var fullPath = ResolvePath(vaultId, path);

        if (File.Exists(fullPath))
        {
            throw new VaultConflictException($"A file already exists at '{path}'.");
        }

        Directory.CreateDirectory(fullPath);
    }

    public bool FolderExists(Guid vaultId, string path)
    {
        var fullPath = ResolvePath(vaultId, path);
        return Directory.Exists(fullPath);
    }

    public void DeleteFolder(Guid vaultId, string path)
    {
        var fullPath = ResolvePath(vaultId, path);
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    public void MoveFolder(Guid vaultId, string fromPath, string toPath)
    {
        var fullFrom = ResolvePath(vaultId, fromPath);
        var fullTo = ResolvePath(vaultId, toPath);

        if (!Directory.Exists(fullFrom))
        {
            throw new DirectoryNotFoundException($"No such folder: {fromPath}");
        }

        if (Directory.Exists(fullTo) || File.Exists(fullTo))
        {
            throw new VaultConflictException($"Something already exists at '{toPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
        Directory.Move(fullFrom, fullTo);
    }

    public void EnsureVaultRoot(Guid vaultId)
    {
        Directory.CreateDirectory(VaultRoot(vaultId));
    }

    public void DeleteVaultRoot(Guid vaultId)
    {
        var root = VaultRoot(vaultId);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void RegisterWrite(string path, string hash)
    {
        _writeMarkers[NormalizeMarkerKey(path)] = (hash, DateTimeOffset.UtcNow);
    }

    public bool WasOurWrite(string path, string hash)
    {
        var key = NormalizeMarkerKey(path);
        if (_writeMarkers.TryGetValue(key, out var marker)
            && string.Equals(marker.Hash, hash, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - marker.RegisteredAt <= MarkerTtl)
        {
            // One-shot: consume the marker so a later genuine external write to the same path
            // isn't accidentally suppressed too.
            _writeMarkers.TryRemove(key, out _);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Shared atomic-write core for <see cref="WriteNoteAtomicAsync"/>, <see cref="WriteAttachmentAtomicAsync"/>,
    /// and <see cref="WriteConflictBlobAsync"/>: write to a temp file in the destination directory,
    /// register the write-marker, then rename over the destination so readers never observe a
    /// partially-written file.
    /// </summary>
    private async Task<(string Sha256, long SizeBytes)> WriteBytesAtomicAsync(
        string fullPath, byte[] bytes, CancellationToken cancellationToken, string? precomputedHash = null)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        // Most callers (NoteService, AttachmentsController) already computed this via
        // ContentHasher to build the DB row before this disk write - same bytes, same algorithm,
        // same lowercase-hex format, so it's safe to reuse rather than hashing twice per write.
        // Lowercase to match the documented contract (IVaultStorage.WriteNoteAtomicAsync) and
        // common tooling conventions - Convert.ToHexString itself yields uppercase.
        var hash = precomputedHash ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

            // Registered before the rename lands: a watcher event firing the instant the rename
            // completes must already find a fresh marker (see RegisterWrite/WasOurWrite docs).
            RegisterWrite(fullPath, hash);

            // Temp file lives in the same directory as the destination, so this rename is a
            // same-volume atomic replace - readers never observe a partially-written file.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only; the original exception is what matters and propagates below.
            }

            throw;
        }

        return (hash, bytes.LongLength);
    }

    private string VaultRoot(Guid vaultId) => Path.Combine(_vaultsRoot, vaultId.ToString());

    private string ConflictBlobPath(Guid vaultId, long revisionId) =>
        Path.Combine(VaultRoot(vaultId), ".slate", "conflicts", $"{revisionId}.md");

    private string ResolvePath(Guid vaultId, string relativePath)
    {
        var validated = SafePath(relativePath);
        var segments = validated.Split('/');
        var parts = new string[segments.Length + 2];
        parts[0] = _vaultsRoot;
        parts[1] = vaultId.ToString();
        Array.Copy(segments, 0, parts, 2, segments.Length);
        return Path.Combine(parts);
    }

    private static void Walk(string currentDir, string relativePrefix, List<VaultEntry> results)
    {
        foreach (var dir in Directory.EnumerateDirectories(currentDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);

            // The reserved conflict-blob subtree; never surfaced as vault content.
            if (relativePrefix.Length == 0 && name.Equals(".slate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relPath = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";
            results.Add(new VaultEntry(relPath, IsDirectory: true));
            Walk(dir, relPath, results);
        }

        foreach (var file in Directory.EnumerateFiles(currentDir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var relPath = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";
            results.Add(new VaultEntry(relPath, IsDirectory: false));
        }
    }

    private static string NormalizeMarkerKey(string path) => Path.GetFullPath(path).ToLowerInvariant();

    private static char[] BuildInvalidSegmentChars()
    {
        var chars = new List<char> { '"', '<', '>', '|', ':', '*', '?', '\\' };
        for (var c = (char)0; c < (char)0x20; c++)
        {
            chars.Add(c);
        }

        return chars.ToArray();
    }
}
