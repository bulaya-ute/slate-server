using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Storage;
using Slate.Server.Vaults;

namespace Slate.Server.Notes;

/// <summary>
/// Note CRUD. Create lives under <c>/api/vaults/{v}/notes</c> (route has a "v" parameter, so
/// <see cref="RequireVaultAccessAttribute"/> applies directly); every other action is keyed by note
/// id alone (<c>/api/notes/{id}/...</c>), so membership is checked manually via
/// <see cref="VaultMembership"/> after resolving the note's vault - see that type's docs for why
/// (mirrors the attribute's 404-not-403 semantics for non-members).
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class NotesController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;
    private readonly NoteService _notes;

    public NotesController(SlateDbContext db, IVaultStorage storage, NoteService notes)
    {
        _db = db;
        _storage = storage;
        _notes = notes;
    }

    [HttpPost("vaults/{v:guid}/notes")]
    [RequireVaultAccess(VaultAccessLevel.Edit)]
    public async Task<IActionResult> Create(Guid v, CreateNoteRequest request, CancellationToken cancellationToken)
    {
        var outcome = await _notes.CreateAsync(v, request.Path, request.Content, CurrentUserId, ResolveDeviceId(), cancellationToken);
        return outcome.Error is { } error
            ? Error(error.StatusCode, error.ErrorCode, error.Message)
            : StatusCode(StatusCodes.Status201Created, outcome.Note);
    }

    [HttpGet("notes/{id:guid}")]
    public async Task<IActionResult> GetMeta(Guid id, CancellationToken cancellationToken)
    {
        var note = await _db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var accessError = await CheckAccessAsync(note.VaultId, VaultAccessLevel.Read, cancellationToken);
        if (accessError is not null)
        {
            return accessError;
        }

        return Ok(new NoteMetaDto(note.Id, note.Path, note.Title, note.HasConflict, note.SizeBytes, note.HeadRevId, note.UpdatedAt));
    }

    [HttpGet("notes/{id:guid}/content")]
    public async Task<IActionResult> GetContent(Guid id, CancellationToken cancellationToken)
    {
        var note = await _db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var accessError = await CheckAccessAsync(note.VaultId, VaultAccessLevel.Read, cancellationToken);
        if (accessError is not null)
        {
            return accessError;
        }

        string content;
        try
        {
            content = await _storage.ReadNoteAsync(note.VaultId, note.Path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Error(StatusCodes.Status500InternalServerError, "io_error", "Note content is missing on disk.");
        }

        Response.Headers["X-Rev-Id"] = note.HeadRevId?.ToString() ?? string.Empty;
        Response.Headers["X-Content-Hash"] = note.ContentHash;
        return Content(content, "text/markdown; charset=utf-8");
    }

    [HttpPut("notes/{id:guid}/content")]
    public async Task<IActionResult> UpdateContent(Guid id, UpdateNoteContentRequest request, CancellationToken cancellationToken)
    {
        var vaultId = await _db.Notes.Where(n => n.Id == id && !n.IsDeleted).Select(n => (Guid?)n.VaultId).FirstOrDefaultAsync(cancellationToken);
        if (vaultId is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var accessError = await CheckAccessAsync(vaultId.Value, VaultAccessLevel.Edit, cancellationToken);
        if (accessError is not null)
        {
            return accessError;
        }

        var deviceId = ResolveDeviceId(request.DeviceId);
        var outcome = await _notes.UpdateContentAsync(id, request.Content, request.BaseRevId, CurrentUserId, deviceId, cancellationToken);

        if (outcome.Error is { } error)
        {
            return Error(error.StatusCode, error.ErrorCode, error.Message);
        }

        if (outcome.IsConflict)
        {
            return new ObjectResult(new UpdateContentConflictDto(outcome.HeadRevId!.Value, outcome.RevId))
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
        }

        return Ok(new UpdateContentSuccessDto(outcome.RevId, outcome.ContentHash!));
    }

    [HttpPost("notes/{id:guid}/rename")]
    public async Task<IActionResult> Rename(Guid id, RenameNoteRequest request, CancellationToken cancellationToken)
    {
        var vaultId = await _db.Notes.Where(n => n.Id == id && !n.IsDeleted).Select(n => (Guid?)n.VaultId).FirstOrDefaultAsync(cancellationToken);
        if (vaultId is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var accessError = await CheckAccessAsync(vaultId.Value, VaultAccessLevel.Edit, cancellationToken);
        if (accessError is not null)
        {
            return accessError;
        }

        var outcome = await _notes.RenameAsync(id, request.NewPath, CurrentUserId, ResolveDeviceId(), cancellationToken);
        return outcome.Error is { } error
            ? Error(error.StatusCode, error.ErrorCode, error.Message)
            : Ok(outcome.Note);
    }

    [HttpDelete("notes/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var vaultId = await _db.Notes.Where(n => n.Id == id && !n.IsDeleted).Select(n => (Guid?)n.VaultId).FirstOrDefaultAsync(cancellationToken);
        if (vaultId is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var accessError = await CheckAccessAsync(vaultId.Value, VaultAccessLevel.Edit, cancellationToken);
        if (accessError is not null)
        {
            return accessError;
        }

        var outcome = await _notes.DeleteAsync(id, CurrentUserId, ResolveDeviceId(), cancellationToken);
        return outcome.Error is { } error
            ? Error(error.StatusCode, error.ErrorCode, error.Message)
            : NoContent();
    }

    /// <summary>
    /// Manual equivalent of <see cref="RequireVaultAccessAttribute"/> for note-id-keyed routes (no
    /// "v" route parameter for the attribute to bind to). Returns null when access is sufficient, or
    /// the exact error response to return otherwise - 404 (not 403) for non-members, matching the
    /// attribute's "don't leak vault existence" behavior.
    /// </summary>
    private async Task<IActionResult?> CheckAccessAsync(Guid vaultId, VaultAccessLevel minimum, CancellationToken cancellationToken)
    {
        var access = await VaultMembership.TryGetAccessAsync(_db, User, vaultId);
        if (access is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        if (!VaultAccess.Satisfies(access.Value, minimum))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to perform this action.");
        }

        return null;
    }

    private string ResolveDeviceId(string? fromBody = null)
    {
        if (!string.IsNullOrWhiteSpace(fromBody))
        {
            return fromBody;
        }

        return Request.Headers.TryGetValue("X-Device-Id", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : "unknown";
    }
}
