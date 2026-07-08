using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Common;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Notes;
using Slate.Server.Storage;
using Slate.Server.Vaults;

namespace Slate.Server.Attachments;

/// <summary>
/// Attachment upload + serving. Upload follows the same dual-write discipline as NoteService (DB
/// transaction -> save -> disk write -> commit); re-uploading to an existing path is an upsert (new
/// hash/size/mime, plus a fresh "attach" revision) rather than a conflict, since attachments aren't
/// versioned/synced the way note content is.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class AttachmentsController : SlateControllerBase
{
    private static readonly FileExtensionContentTypeProvider MimeProvider = new();

    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;
    private readonly IRevisionBroadcaster _broadcaster;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(SlateDbContext db, IVaultStorage storage, IRevisionBroadcaster broadcaster, ILogger<AttachmentsController> logger)
    {
        _db = db;
        _storage = storage;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    [HttpPost("vaults/{v:guid}/attachments")]
    [RequireVaultAccess(VaultAccessLevel.Edit)]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid v, IFormFile? file, [FromForm] string? folder, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "file is required.");
        }

        var fileName = System.IO.Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "file name is required.");
        }

        var rawPath = string.IsNullOrWhiteSpace(folder) ? fileName : $"{folder.Trim().TrimEnd('/')}/{fileName}";

        string path;
        try
        {
            path = _storage.SafePath(rawPath);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var (hash, size) = ContentHasher.Compute(bytes);
        var mime = ResolveMime(file, path);
        var now = DateTimeOffset.UtcNow;
        var deviceId = ResolveDeviceId();

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.VaultId == v && a.Path == path, cancellationToken);
            if (attachment is null)
            {
                attachment = new Attachment
                {
                    Id = Guid.NewGuid(),
                    VaultId = v,
                    Path = path,
                    ContentHash = hash,
                    SizeBytes = size,
                    Mime = mime,
                    CreatedAt = now,
                };
                _db.Attachments.Add(attachment);
            }
            else
            {
                attachment.ContentHash = hash;
                attachment.SizeBytes = size;
                attachment.Mime = mime;
            }

            var revision = new Revision
            {
                VaultId = v,
                NoteId = null,
                AuthorId = CurrentUserId,
                DeviceId = deviceId,
                Kind = RevisionKind.Attach,
                Path = path,
                ContentHash = hash,
                IsConflict = false,
                CreatedAt = now,
            };
            _db.Revisions.Add(revision);

            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _storage.WriteAttachmentAtomicAsync(v, path, bytes, cancellationToken);
            }
            catch (IOException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex,
                    "Failed to write attachment '{Path}' to disk in vault {VaultId}; DB changes rolled back.", path, v);
                return Error(StatusCodes.Status500InternalServerError, "io_error", "Failed to write attachment to disk.");
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "MANUAL RECONCILIATION REQUIRED for vault {VaultId}: attachment commit failed for '{Path}' " +
                    "after the file was already written to disk.", v, path);
                return Error(StatusCodes.Status500InternalServerError, "io_error", "Failed to persist attachment.");
            }

            await _broadcaster.BroadcastAsync(
                new RevisionBroadcast(v, null, revision.Id, "attach", path, null, hash, deviceId, false, now),
                cancellationToken);

            return StatusCode(StatusCodes.Status201Created, new AttachmentDto(path, size, mime));
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    [HttpGet("vaults/{v:guid}/files/{**path}")]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> GetFile(Guid v, string path, CancellationToken cancellationToken)
    {
        string safePath;
        try
        {
            safePath = _storage.SafePath(path);
        }
        catch (VaultPathException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message);
        }

        byte[] bytes;
        try
        {
            bytes = await _storage.ReadAttachmentAsync(v, safePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such file.");
        }

        var mime = await _db.Attachments.Where(a => a.VaultId == v && a.Path == safePath)
            .Select(a => a.Mime)
            .FirstOrDefaultAsync(cancellationToken)
            ?? GuessMimeFromExtension(safePath);

        return File(bytes, mime);
    }

    private static string ResolveMime(IFormFile file, string path) =>
        string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType == "application/octet-stream"
            ? GuessMimeFromExtension(path)
            : file.ContentType;

    private static string GuessMimeFromExtension(string path) =>
        MimeProvider.TryGetContentType(path, out var contentType) ? contentType : "application/octet-stream";

    private string ResolveDeviceId() =>
        Request.Headers.TryGetValue("X-Device-Id", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : "unknown";
}
