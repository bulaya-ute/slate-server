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
/// sync: move/remove the files, then update every affected note row and append its revision.
/// </summary>
[ApiController]
[Route("api/vaults/{v:guid}")]
[Authorize]
public class TreeController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;

    public TreeController(SlateDbContext db, IVaultStorage storage)
    {
        _db = db;
        _storage = storage;
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

        try
        {
            _storage.CreateFolder(v, request.Path);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }
        catch (IOException)
        {
            return Error(StatusCodes.Status409Conflict, "folder_conflict", "A file already exists at that path.");
        }

        return NoContent();
    }

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

        var affectedNotes = await FindNotesUnderFolderAsync(v, oldPath, cancellationToken);
        var deviceId = ResolveDeviceId();
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Moves the whole directory tree on disk in one shot, bringing every contained file
            // (and any note-less empty subfolders) along without a per-file Move call.
            _storage.MoveFolder(v, oldPath, newPath);
        }
        catch (IOException)
        {
            return Error(StatusCodes.Status409Conflict, "folder_conflict", "Something already exists at the destination path.");
        }

        var prefix = oldPath + "/";
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

        return NoContent();
    }

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

        foreach (var note in affectedNotes)
        {
            await _storage.DeleteAsync(v, note.Path, cancellationToken);

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

        // Sweeps whatever remains on disk: note-less empty subfolders, stray non-note files, and
        // the folder itself (the notes above were already removed individually).
        _storage.DeleteFolder(v, folderPath);

        return NoContent();
    }

    /// <summary>
    /// Notes whose path is exactly <paramref name="folderPath"/> or nested under it. Filtered in
    /// memory (not via a SQL LIKE) so a folder name containing '%' or '_' can't be misinterpreted
    /// as a wildcard.
    /// </summary>
    private async Task<List<Note>> FindNotesUnderFolderAsync(Guid vaultId, string folderPath, CancellationToken cancellationToken)
    {
        var prefix = folderPath + "/";
        var allNotes = await _db.Notes.Where(n => n.VaultId == vaultId && !n.IsDeleted).ToListAsync(cancellationToken);
        return allNotes.Where(n => n.Path == folderPath || n.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private string ResolveDeviceId() =>
        Request.Headers.TryGetValue("X-Device-Id", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : "unknown";
}
