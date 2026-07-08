using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Notes;
using Slate.Server.Storage;
using Slate.Server.Vaults;

namespace Slate.Server.Links;

/// <summary>Backlinks (per note) and the vault-wide link graph, both derived from the `links` table.</summary>
[ApiController]
[Route("api")]
[Authorize]
public class GraphController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;

    public GraphController(SlateDbContext db, IVaultStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    [HttpGet("notes/{id:guid}/backlinks")]
    public async Task<IActionResult> GetBacklinks(Guid id, CancellationToken cancellationToken)
    {
        var note = await _db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        var access = await VaultMembership.TryGetAccessAsync(_db, User, note.VaultId);
        if (access is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_found", "No such note.");
        }

        if (!VaultAccess.Satisfies(access.Value, VaultAccessLevel.Read))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to perform this action.");
        }

        var incoming = await _db.Links
            .Where(l => l.TargetNoteId == note.Id && !l.SourceNote!.IsDeleted)
            .Select(l => new { l.TargetText, SourceId = l.SourceNote!.Id, SourcePath = l.SourceNote.Path, SourceTitle = l.SourceNote.Title })
            .ToListAsync(cancellationToken);

        var results = new List<BacklinkDto>(incoming.Count);
        foreach (var link in incoming)
        {
            var snippet = await BuildContextSnippetAsync(note.VaultId, link.SourcePath, link.TargetText, cancellationToken);
            results.Add(new BacklinkDto(link.SourceId, link.SourcePath, link.SourceTitle, snippet));
        }

        return Ok(results);
    }

    [HttpGet("vaults/{v:guid}/graph")]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> GetGraph(Guid v, CancellationToken cancellationToken)
    {
        var notes = await _db.Notes
            .Where(n => n.VaultId == v && !n.IsDeleted)
            .Select(n => new { n.Id, n.Path, n.Title })
            .ToListAsync(cancellationToken);

        var resolvedEdges = await _db.Links
            .Where(l => l.TargetNoteId != null && l.SourceNote!.VaultId == v && !l.SourceNote.IsDeleted && !l.TargetNote!.IsDeleted)
            .Select(l => new { Source = l.SourceNoteId, Target = l.TargetNoteId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken);

        var degree = new Dictionary<Guid, int>();
        foreach (var edge in resolvedEdges)
        {
            degree[edge.Source] = degree.GetValueOrDefault(edge.Source) + 1;
            degree[edge.Target] = degree.GetValueOrDefault(edge.Target) + 1;
        }

        var nodes = notes
            .Select(n => new GraphNodeDto(n.Id, n.Path, n.Title, degree.GetValueOrDefault(n.Id)))
            .ToList();
        var edges = resolvedEdges.Select(e => new GraphEdgeDto(e.Source, e.Target)).ToList();

        return Ok(new GraphResponse(nodes, edges));
    }

    /// <summary>
    /// A short window of plain text around a link's occurrence in the source note's content, read
    /// fresh off disk (the `links` table only stores the raw target text, not surrounding context).
    /// Falls back to the start of the note if the exact text can't be located (e.g. content changed
    /// since the note was last indexed).
    /// </summary>
    private async Task<string> BuildContextSnippetAsync(Guid vaultId, string sourcePath, string targetText, CancellationToken cancellationToken)
    {
        string plain;
        try
        {
            var content = await _storage.ReadNoteAsync(vaultId, sourcePath, cancellationToken);
            plain = MarkdownIndexer.StripToPlainText(content);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }

        const int window = 60;
        var index = plain.IndexOf(targetText, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return plain.Length <= window * 2
                ? plain.Trim()
                : plain[..(window * 2)].Trim() + "…";
        }

        var start = Math.Max(0, index - window);
        var end = Math.Min(plain.Length, index + targetText.Length + window);
        var snippet = plain[start..end].Trim();

        return (start > 0 ? "…" : string.Empty) + snippet + (end < plain.Length ? "…" : string.Empty);
    }
}
