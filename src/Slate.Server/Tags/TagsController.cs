using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Vaults;

namespace Slate.Server.Tags;

/// <summary>
/// Tag listing/browsing. Counts and the note list are both driven by a live join through
/// <see cref="Note.NoteTags"/> filtered to non-deleted notes, rather than iterating the `tags` table
/// directly - a tag with zero remaining live notes (e.g. its only note was deleted) simply drops out
/// of both results without needing any explicit cleanup of the `tags`/`note_tags` rows themselves.
/// </summary>
[ApiController]
[Route("api/vaults/{v:guid}/tags")]
[Authorize]
public class TagsController : SlateControllerBase
{
    private readonly SlateDbContext _db;

    public TagsController(SlateDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> GetTags(Guid v, CancellationToken cancellationToken)
    {
        // Ordered on the anonymous projection (translatable) rather than after mapping to TagDto -
        // EF can't translate an OrderBy that reads a property off a client-side record constructor.
        var grouped = await _db.NoteTags
            .Where(nt => nt.Tag!.VaultId == v && !nt.Note!.IsDeleted)
            .GroupBy(nt => nt.Tag!.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return Ok(grouped.Select(g => new TagDto(g.Name, g.Count)));
    }

    [HttpGet("{tag}/notes")]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> GetNotesForTag(Guid v, string tag, CancellationToken cancellationToken)
    {
        var normalized = tag.Trim().ToLowerInvariant();

        var notes = await _db.NoteTags
            .Where(nt => nt.Tag!.VaultId == v && nt.Tag!.Name == normalized && !nt.Note!.IsDeleted)
            .OrderBy(nt => nt.Note!.Path)
            .Select(nt => new NoteSummaryDto(nt.Note!.Id, nt.Note.Path, nt.Note.Title, nt.Note.HasConflict, nt.Note.SizeBytes, nt.Note.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(notes);
    }
}
