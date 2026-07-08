using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Storage;

namespace Slate.Server.Vaults;

/// <summary>
/// Vault CRUD. Listing/creating are scoped to the caller's own memberships (any authenticated user
/// may create a vault - they become its owner); rename/delete require Owner-or-Admin, enforced via
/// <see cref="RequireVaultAccessAttribute"/>.
/// </summary>
[ApiController]
[Route("api/vaults")]
[Authorize]
public class VaultsController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;
    private readonly ILogger<VaultsController> _logger;

    public VaultsController(SlateDbContext db, IVaultStorage storage, ILogger<VaultsController> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId;

        var memberships = await _db.VaultMembers
            .Where(m => m.UserId == userId)
            .Select(m => new
            {
                m.Access,
                m.Vault!.Id,
                m.Vault!.Name,
                m.Vault!.CreatedAt,
                NoteCount = m.Vault!.Notes.Count(n => !n.IsDeleted),
                SizeBytes = m.Vault!.Notes.Where(n => !n.IsDeleted).Sum(n => (long?)n.SizeBytes) ?? 0,
            })
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(memberships.Select(m =>
            new VaultDto(m.Id, m.Name, m.Access.ToString().ToLowerInvariant(), m.NoteCount, m.SizeBytes, m.CreatedAt)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateVaultRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "name is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var vault = new Vault
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            OwnerId = CurrentUserId,
            CreatedAt = now,
        };

        _db.Vaults.Add(vault);
        _db.VaultMembers.Add(new VaultMember
        {
            VaultId = vault.Id,
            UserId = CurrentUserId,
            Access = VaultAccessLevel.Owner,
        });

        await _db.SaveChangesAsync(cancellationToken);
        _storage.EnsureVaultRoot(vault.Id);

        return StatusCode(StatusCodes.Status201Created,
            new VaultDto(vault.Id, vault.Name, "owner", NoteCount: 0, SizeBytes: 0, vault.CreatedAt));
    }

    [HttpPatch("{v:guid}")]
    [RequireVaultAccess(VaultAccessLevel.Owner)]
    public async Task<IActionResult> Rename(Guid v, RenameVaultRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "name is required.");
        }

        var vault = await _db.Vaults.SingleAsync(x => x.Id == v, cancellationToken);
        vault.Name = request.Name.Trim();
        await _db.SaveChangesAsync(cancellationToken);

        var noteCount = await _db.Notes.CountAsync(n => n.VaultId == v && !n.IsDeleted, cancellationToken);
        var sizeBytes = await _db.Notes.Where(n => n.VaultId == v && !n.IsDeleted)
            .SumAsync(n => (long?)n.SizeBytes, cancellationToken) ?? 0;

        var access = (VaultAccessLevel)HttpContext.Items[RequireVaultAccessAttribute.HttpContextItemKey]!;
        return Ok(new VaultDto(vault.Id, vault.Name, access.ToString().ToLowerInvariant(), noteCount, sizeBytes, vault.CreatedAt));
    }

    [HttpDelete("{v:guid}")]
    [RequireVaultAccess(VaultAccessLevel.Owner)]
    public async Task<IActionResult> Delete(Guid v, CancellationToken cancellationToken)
    {
        var vault = await _db.Vaults.SingleAsync(x => x.Id == v, cancellationToken);

        // Cascade FKs on notes/revisions/attachments/tags/vault_members (see VaultConfiguration)
        // handle every DB row; only the on-disk content directory needs explicit cleanup.
        _db.Vaults.Remove(vault);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            _storage.DeleteVaultRoot(v);
        }
        catch (Exception ex)
        {
            // DB rows are already gone (cascade-deleted) and that's the source of truth for the
            // app, so this isn't fatal to the request - but nothing else will ever retry this
            // cleanup, so a loud log is the only way an admin finds out the directory needs
            // manual removal.
            _logger.LogError(ex,
                "Failed to delete on-disk content directory for vault {VaultId} after its DB row was removed; " +
                "directory is orphaned and needs manual cleanup.",
                v);
        }

        return NoContent();
    }
}
