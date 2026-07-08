using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Storage;

namespace Slate.Server.Vaults;

/// <summary>
/// The vault's folder/note tree (one call) plus folder create/rename/delete. Folders aren't rows
/// in the database at all - they're purely a filesystem concept, discovered via
/// <see cref="IVaultStorage.ListAll"/> - while notes are tracked in Postgres (path, title, etc.)
/// with file content living on disk. A folder rename/delete therefore has to keep both sides in
/// sync: update every affected note row and append its revision, then move/remove the files - see
/// the invariant notes on <see cref="RenameFolder"/> and <see cref="DeleteFolder"/> for why that
/// order (and the compensating actions around it) matters.
/// </summary>
[ApiController]
[Route("api/vaults/{v:guid}")]
[Authorize]
public class TreeController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;
    private readonly ILogger<TreeController> _logger;

    public TreeController(SlateDbContext db, IVaultStorage storage, ILogger<TreeController> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    [HttpGet("tree")]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> GetTree(Guid v, CancellationToken cancellationToken)
    {
        var folders = _storage.ListAll(v)
            .Where(e => e.IsDirectory)
            .Select(e => e.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var notes = await _db.Notes
            .Where(n => n.VaultId == v && !n.IsDeleted)
            .OrderBy(n => n.Path)
            .Select(n => new NoteSummaryDto(n.Id, n.Path, n.Title, n.HasConflict, n.SizeBytes, n.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(new TreeResponse(folders, notes));
    }

    [HttpPost("folders")]
    [RequireVaultAccess(VaultAccessLevel.Edit)]
    public IActionResult CreateFolder(Guid v, CreateFolderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "path is required.");
        }

        string path;
        try
        {
            path = _storage.SafePath(request.Path);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }

        // Windows/macOS filesystems collapse case (so "Notes" and "notes" are the same directory
        // entry there) but Postgres and Linux prod don't - catch the mismatch at the app level
        // rather than let it manifest as a confusing disk-level failure or silent divergence.
        if (VaultPathCollision.HasCaseOnlyCollision(_storage, v, path))
        {
            return Error(StatusCodes.Status409Conflict, "case_conflict",
                "A file or folder with the same name (different case) already exists.");
        }

        try
        {
            _storage.CreateFolder(v, path);
        }
        catch (VaultConflictException)
        {
            return Error(StatusCodes.Status409Conflict, "folder_conflict", "A file already exists at that path.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Unexpected IO error creating folder '{Path}' in vault {VaultId}.", path, v);
            return Error(StatusCodes.Status500InternalServerError, "io_error", "Failed to create folder on disk.");
        }

        return NoContent();
    }

    /// <summary>
    /// Renames a folder, which means moving every note under it and appending a Rename revision
    /// for each. The invariant that matters here: the DB must never end up referencing file
    /// locations that don't exist on disk. So note path updates + revision inserts are written to
    /// an open transaction FIRST, then flushed with <see cref="DbContext.SaveChangesAsync"/>, and
    /// only THEN does the disk move happen - the transaction is committed only after that succeeds:
    /// <list type="bullet">
    /// <item>Disk move fails: the transaction is rolled back, so DB and disk are both left exactly
    /// as they were, and the request returns an error.</item>
    /// <item>Disk move succeeds but the commit itself then fails (rare - e.g. the DB connection
    /// drops mid-commit): a compensating move-back is attempted so disk matches the (uncommitted)
    /// DB state again. If that compensating move also fails, the vault is now split between two
    /// paths with no automatic fix, so it's logged at Critical with everything needed for a human
    /// to reconcile it by hand.</item>
    /// </list>
    /// </summary>
    [HttpPost("folders/rename")]
    [RequireVaultAccess(VaultAccessLevel.Edit)]
    public async Task<IActionResult> RenameFolder(Guid v, RenameFolderRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || string.IsNullOrWhiteSpace(request.NewPath))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "path and newPath are required.");
        }

        string oldPath, newPath;
        try
        {
            oldPath = _storage.SafePath(request.Path);
            newPath = _storage.SafePath(request.NewPath);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }

        if (!_storage.FolderExists(v, oldPath))
        {
            return Error(StatusCodes.Status404NotFound, "folder_not_found", "No such folder.");
        }

        if (VaultPathCollision.HasCaseOnlyCollision(_storage, v, newPath, excludePrefix: oldPath))
        {
            return Error(StatusCodes.Status409Conflict, "case_conflict",
                "A file or folder with the same name (different case) already exists at the destination.");
        }

        var affectedNotes = await FindNotesUnderFolderAsync(v, oldPath, cancellationToken);
        var deviceId = ResolveDeviceId();
        var now = DateTimeOffset.UtcNow;
        var prefix = oldPath + "/";

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var note in affectedNotes)
            {
                var newNotePath = note.Path == oldPath
                    ? newPath
                    : newPath + "/" + note.Path[prefix.Length..];

                var revision = new Revision
                {
                    VaultId = v,
                    Note = note,
                    ParentRevId = note.HeadRevId,
                    AuthorId = CurrentUserId,
                    DeviceId = deviceId,
                    Kind = RevisionKind.Rename,
                    Path = newNotePath,
                    OldPath = note.Path,
                    ContentHash = note.ContentHash,
                    IsConflict = false,
                    CreatedAt = now,
                };

                _db.Revisions.Add(revision);
                note.Path = newNotePath;
                note.UpdatedAt = now;
                note.HeadRevision = revision;
            }

            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                // Moves the whole directory tree on disk in one shot, bringing every contained
                // file (and any note-less empty subfolders) along without a per-file Move call.
                _storage.MoveFolder(v, oldPath, newPath);
            }
            catch (VaultConflictException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Error(StatusCodes.Status409Conflict, "folder_conflict",
                    "Something already exists at the destination path.");
            }
            catch (IOException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex,
                    "Unexpected IO error moving folder '{OldPath}' -> '{NewPath}' in vault {VaultId}; DB changes rolled back.",
                    oldPath, newPath, v);
                return Error(StatusCodes.Status500InternalServerError, "io_error", "Failed to move folder on disk.");
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Disk already moved but the DB transaction never committed - the two sides now
                // disagree. Try to move the folder back so disk matches the (never-committed,
                // effectively rolled-back) DB state again.
                try
                {
                    _storage.MoveFolder(v, newPath, oldPath);
                    _logger.LogError(ex,
                        "Folder rename commit failed for vault {VaultId} ('{OldPath}' -> '{NewPath}'); " +
                        "compensating move-back succeeded, disk and DB are consistent again.",
                        v, oldPath, newPath);
                }
                catch (Exception compensationEx)
                {
                    _logger.LogCritical(compensationEx,
                        "MANUAL RECONCILIATION REQUIRED for vault {VaultId}: folder rename commit failed " +
                        "('{OldPath}' -> '{NewPath}', commit error: {CommitError}) AND the compensating " +
                        "move-back also failed. Disk now has the folder at '{NewPath}' while the database " +
                        "still references '{OldPath}' for {NoteCount} note(s).",
                        v, oldPath, newPath, ex.Message, newPath, oldPath, affectedNotes.Count);
                }

                return Error(StatusCodes.Status500InternalServerError, "io_error", "Failed to persist folder rename.");
            }

            return NoContent();
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch
            {
                // Best-effort cleanup only; must not mask whichever response was already decided above.
            }
        }
    }

    /// <summary>
    /// Deletes a folder: every note under it is soft-deleted (with a Delete revision appended) and
    /// only THEN is anything removed from disk - the opposite order from before, which deleted
    /// files per-note ahead of <see cref="DbContext.SaveChangesAsync"/>. That earlier order had the
    /// worse failure mode: a mid-loop failure left file-less notes still marked live in the DB, a
    /// broken read for any concurrent reader (row present, content gone). Soft-delete-first instead
    /// commits the DB truth (these notes are gone) as a single transaction, and only then attempts
    /// disk cleanup, which is best-effort from there on - a failure removing any given file/folder
    /// just orphans it, logged as a Warning (benign and recoverable; nothing reads a soft-deleted
    /// note's content through the DB anymore, so a stray file lingering on disk breaks nothing).
    ///
    /// Note for S5's file watcher: once this endpoint commits, affected notes are IsDeleted = true
    /// even though their files may still be on disk for a brief window (or indefinitely, if the
    /// best-effort delete below fails) - the watcher must ignore filesystem events for paths
    /// belonging to soft-deleted notes rather than resurrecting them as new creates.
    /// </summary>
    [HttpDelete("folders")]
    [RequireVaultAccess(VaultAccessLevel.Edit)]
    public async Task<IActionResult> DeleteFolder(Guid v, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "path is required.");
        }

        string folderPath;
        try
        {
            folderPath = _storage.SafePath(path);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }

        if (!_storage.FolderExists(v, folderPath))
        {
            return Error(StatusCodes.Status404NotFound, "folder_not_found", "No such folder.");
        }

        var affectedNotes = await FindNotesUnderFolderAsync(v, folderPath, cancellationToken);
        var deviceId = ResolveDeviceId();
        var now = DateTimeOffset.UtcNow;

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            foreach (var note in affectedNotes)
            {
                var revision = new Revision
                {
                    VaultId = v,
                    Note = note,
                    ParentRevId = note.HeadRevId,
                    AuthorId = CurrentUserId,
                    DeviceId = deviceId,
                    Kind = RevisionKind.Delete,
                    Path = note.Path,
                    ContentHash = note.ContentHash,
                    IsConflict = false,
                    CreatedAt = now,
                };

                _db.Revisions.Add(revision);
                note.IsDeleted = true;
                note.UpdatedAt = now;
                note.HeadRevision = revision;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // From here on, rows are already soft-deleted and committed - disk cleanup is best-effort.
        // A failure deleting any individual file (or the final folder sweep) just leaves an
        // orphaned file/directory on disk; that's benign and recoverable later, unlike the
        // inverse (a live row with no file), so it's a Warning, not an error returned to the caller.
        foreach (var note in affectedNotes)
        {
            try
            {
                await _storage.DeleteAsync(v, note.Path, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete on-disk file for soft-deleted note '{NotePath}' in vault {VaultId}; " +
                    "file is orphaned and can be cleaned up later.",
                    note.Path, v);
            }
        }

        try
        {
            // Sweeps whatever remains on disk: note-less empty subfolders, stray non-note files,
            // and the folder itself (the notes above were already removed individually).
            _storage.DeleteFolder(v, folderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete folder '{FolderPath}' in vault {VaultId} on disk after soft-deleting its " +
                "notes; contents are orphaned and can be cleaned up later.",
                folderPath, v);
        }

        return NoContent();
    }

    /// <summary>
    /// Notes whose path is exactly <paramref name="folderPath"/> or nested under it. Translated
    /// directly to SQL: EF Core parameterizes and escapes LIKE wildcard characters (%, _) found
    /// inside <c>prefix</c> itself when translating <see cref="string.StartsWith(string)"/>, so a
    /// folder name containing one of those characters can't be misinterpreted as a wildcard.
    /// </summary>
    private async Task<List<Note>> FindNotesUnderFolderAsync(Guid vaultId, string folderPath, CancellationToken cancellationToken)
    {
        var prefix = folderPath + "/";
        return await _db.Notes
            .Where(n => n.VaultId == vaultId && !n.IsDeleted && (n.Path == folderPath || n.Path.StartsWith(prefix)))
            .ToListAsync(cancellationToken);
    }

    private string ResolveDeviceId() =>
        Request.Headers.TryGetValue("X-Device-Id", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : "unknown";
}
