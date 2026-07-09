using Microsoft.EntityFrameworkCore;
using Slate.Server.Common;
using Slate.Server.Data;
using Slate.Server.Domain;
using Slate.Server.Storage;
using Slate.Server.Vaults;

namespace Slate.Server.Notes;

/// <summary>
/// Notes CRUD: every mutation here writes disk via <see cref="IVaultStorage"/>, updates the note's
/// row, reindexes (tags/links/search_vector), and appends a revision - replicating the dual-write
/// discipline established in Vaults/TreeController.cs (validate first, then DB transaction -> save
/// -> disk write -> commit, with compensation on a late failure). <see cref="IRevisionBroadcaster"/>
/// is called after every successful commit so S5 can drop in a real SignalR-backed implementation
/// later without touching any of these call sites.
/// </summary>
public class NoteService
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;
    private readonly IRevisionBroadcaster _broadcaster;
    private readonly ILogger<NoteService> _logger;

    public NoteService(SlateDbContext db, IVaultStorage storage, IRevisionBroadcaster broadcaster, ILogger<NoteService> logger)
    {
        _db = db;
        _storage = storage;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task<NoteOperationResult> CreateAsync(
        Guid vaultId, string? rawPath, string? content, Guid authorId, string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return NoteOperationResult.Fail(400, "invalid_request", "path is required.");
        }

        string path;
        try
        {
            path = _storage.SafePath(rawPath);
        }
        catch (VaultPathException ex)
        {
            return NoteOperationResult.Fail(400, "invalid_path", ex.Message);
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return NoteOperationResult.Fail(400, "invalid_path", "Note paths must end with '.md'.");
        }

        if (await _db.Notes.AnyAsync(n => n.VaultId == vaultId && n.Path == path && !n.IsDeleted, cancellationToken))
        {
            return NoteOperationResult.Fail(409, "note_exists", "A note already exists at that path.");
        }

        if (VaultPathCollision.HasCaseOnlyCollision(_storage, vaultId, path))
        {
            return NoteOperationResult.Fail(409, "case_conflict",
                "A file or folder with the same name (different case) already exists.");
        }

        content ??= string.Empty;
        var (hash, size) = ContentHasher.Compute(content);
        var fallbackTitle = System.IO.Path.GetFileNameWithoutExtension(path);
        var extracted = MarkdownIndexer.Extract(content, fallbackTitle);
        var plainText = MarkdownIndexer.StripToPlainText(content);
        var now = DateTimeOffset.UtcNow;

        var note = new Note
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            Path = path,
            Title = extracted.Title,
            ContentHash = hash,
            SizeBytes = size,
            HasConflict = false,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _db.Notes.Add(note);
            await _db.SaveChangesAsync(cancellationToken);

            var revision = new Revision
            {
                VaultId = vaultId,
                NoteId = note.Id,
                ParentRevId = null,
                AuthorId = authorId,
                DeviceId = deviceId,
                Kind = RevisionKind.Create,
                Path = path,
                ContentHash = hash,
                IsConflict = false,
                CreatedAt = now,
            };
            _db.Revisions.Add(revision);
            await _db.SaveChangesAsync(cancellationToken);

            note.HeadRevId = revision.Id;
            await ReindexNoteAsync(note, extracted, cancellationToken);
            await UpdateSearchVectorAsync(note.Id, note.Title, plainText, cancellationToken);
            await ResolveIncomingLinksAsync(vaultId, note, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _storage.WriteNoteAtomicAsync(vaultId, path, content, cancellationToken, precomputedHash: hash);
            }
            catch (IOException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex,
                    "Failed to write note '{Path}' to disk in vault {VaultId}; DB changes rolled back.", path, vaultId);
                return NoteOperationResult.Fail(500, "io_error", "Failed to write note to disk.");
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    await _storage.DeleteAsync(vaultId, path, cancellationToken);
                    _logger.LogError(ex,
                        "Note create commit failed for vault {VaultId} path '{Path}'; compensating delete succeeded.",
                        vaultId, path);
                }
                catch (Exception compensationEx)
                {
                    _logger.LogCritical(compensationEx,
                        "MANUAL RECONCILIATION REQUIRED for vault {VaultId}: note create commit failed for '{Path}' " +
                        "(error: {CommitError}) AND the compensating delete also failed.",
                        vaultId, path, ex.Message);
                }

                return NoteOperationResult.Fail(500, "io_error", "Failed to persist note creation.");
            }
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        await BroadcastAsync(vaultId, note.Id, note.HeadRevId!.Value, "create", path, null, hash, deviceId, false, now, cancellationToken);
        return NoteOperationResult.Ok(ToMetaDto(note));
    }

    public async Task<UpdateContentOutcome> UpdateContentAsync(
        Guid noteId, string? content, long? baseRevId, Guid authorId, string deviceId, CancellationToken cancellationToken)
    {
        if (baseRevId is null)
        {
            return UpdateContentOutcome.Fail(400, "invalid_request", "baseRevId is required.");
        }

        content ??= string.Empty;

        var note = await _db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return UpdateContentOutcome.Fail(404, "not_found", "No such note.");
        }

        var (hash, size) = ContentHasher.Compute(content);
        var now = DateTimeOffset.UtcNow;

        // Cheap early-out: if the head has already visibly moved, skip straight to the conflict
        // path without the transaction/rollback dance below. This is NOT the race guard - see
        // the comment on the atomic claim in ApplyFastPathEditAsync for that - just an
        // optimization for the common, non-racing stale-client case.
        if (note.HeadRevId == baseRevId)
        {
            var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            UpdateContentOutcome? fastPathResult;
            try
            {
                fastPathResult = await ApplyFastPathEditAsync(
                    transaction, note, content, hash, size, baseRevId.Value, authorId, deviceId, now, cancellationToken);
            }
            finally
            {
                await transaction.DisposeAsync();
            }

            if (fastPathResult is not null)
            {
                return fastPathResult;
            }

            // Lost the atomic head claim to a concurrent writer: ApplyFastPathEditAsync already
            // rolled back this transaction (discarding our speculative revision insert), so the
            // tracked `note` instance's HeadRevId is now stale relative to the DB - reload it so
            // RecordConflictAsync below reports the winner's actual head, then fall through to
            // record a proper conflict in a fresh transaction.
            await _db.Entry(note).ReloadAsync(cancellationToken);
        }

        var conflictTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            return await RecordConflictAsync(conflictTransaction, note, content, hash, baseRevId.Value, authorId, deviceId, now, cancellationToken);
        }
        finally
        {
            await conflictTransaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Attempts the fast-path (non-conflict) edit. Returns null if a concurrent writer claimed the
    /// head first - see the atomic claim below - in which case the caller must treat this exactly
    /// like a stale baseRevId and re-route into <see cref="RecordConflictAsync"/>.
    /// </summary>
    private async Task<UpdateContentOutcome?> ApplyFastPathEditAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Note note, string content, string hash, long size, long baseRevId, Guid authorId, string deviceId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fallbackTitle = System.IO.Path.GetFileNameWithoutExtension(note.Path);
        var extracted = MarkdownIndexer.Extract(content, fallbackTitle);
        var plainText = MarkdownIndexer.StripToPlainText(content);

        var revision = new Revision
        {
            VaultId = note.VaultId,
            NoteId = note.Id,
            ParentRevId = baseRevId,
            AuthorId = authorId,
            DeviceId = deviceId,
            Kind = RevisionKind.Edit,
            Path = note.Path,
            ContentHash = hash,
            IsConflict = false,
            CreatedAt = now,
        };
        _db.Revisions.Add(revision);
        await _db.SaveChangesAsync(cancellationToken);

        // The actual race guard (mirrors the invite-claim pattern in AuthController.Register):
        // the C# "note.HeadRevId == baseRevId" check the caller already did is a plain
        // read-then-compare under Read Committed, so two concurrent PUTs with the same valid
        // baseRevId can both pass it and both reach here. Only one of them may claim the head -
        // done as a single conditional UPDATE (WHERE id AND head_rev_id = baseRevId) rather than
        // a SELECT ... FOR UPDATE because it composes cleanly with the existing insert-then-claim
        // shape and needs no extra locking statement: Postgres already serializes concurrent
        // UPDATEs of the same row, so the loser's UPDATE blocks until the winner commits, then
        // re-evaluates the WHERE against the now-committed row and affects zero rows - never a
        // lost update. Deliberately does NOT touch note's change-tracked properties directly
        // (no `note.HeadRevId = ...`); if the claim below fails and this transaction rolls back,
        // any tracked in-memory mutation would still be sitting on the shared `note` instance and
        // could get flushed as an unconditional overwrite by a later SaveChangesAsync.
        var claimed = await _db.Notes
            .Where(n => n.Id == note.Id && n.HeadRevId == baseRevId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(n => n.HeadRevId, revision.Id)
                .SetProperty(n => n.ContentHash, hash)
                .SetProperty(n => n.SizeBytes, size)
                .SetProperty(n => n.Title, extracted.Title)
                .SetProperty(n => n.UpdatedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await ReindexNoteAsync(note, extracted, cancellationToken);
        await UpdateSearchVectorAsync(note.Id, extracted.Title, plainText, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        string? previousContent;
        try
        {
            previousContent = await _storage.ReadNoteAsync(note.VaultId, note.Path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // No prior file on disk (shouldn't normally happen for a note with a head revision) -
            // nothing to restore later if the commit below fails.
            previousContent = null;
        }

        try
        {
            await _storage.WriteNoteAtomicAsync(note.VaultId, note.Path, content, cancellationToken, precomputedHash: hash);
        }
        catch (IOException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex,
                "Failed to write note '{Path}' to disk in vault {VaultId}; DB changes rolled back.", note.Path, note.VaultId);
            return UpdateContentOutcome.Fail(500, "io_error", "Failed to write note to disk.");
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            try
            {
                if (previousContent is not null)
                {
                    await _storage.WriteNoteAtomicAsync(note.VaultId, note.Path, previousContent, cancellationToken);
                }

                _logger.LogError(ex,
                    "Note update commit failed for note {NoteId}; compensating restore succeeded.", note.Id);
            }
            catch (Exception compensationEx)
            {
                _logger.LogCritical(compensationEx,
                    "MANUAL RECONCILIATION REQUIRED for note {NoteId} ('{Path}'): update commit failed " +
                    "(error: {CommitError}) AND the compensating restore also failed.",
                    note.Id, note.Path, ex.Message);
            }

            return UpdateContentOutcome.Fail(500, "io_error", "Failed to persist note update.");
        }

        await BroadcastAsync(note.VaultId, note.Id, revision.Id, "edit", note.Path, null, hash, deviceId, false, now, cancellationToken);
        return UpdateContentOutcome.Success(revision.Id, hash);
    }

    /// <summary>
    /// Sync protocol conflict path (design spec): the client's baseRevId no longer matches the
    /// note's head, meaning someone else's edit landed first. The incoming content is stored as a
    /// conflict blob under .slate/conflicts/{revId}.md - never written to the head file - a revision
    /// with is_conflict=true is appended, and notes.has_conflict is set. Nothing about the note's
    /// head content/path/title changes.
    /// </summary>
    private async Task<UpdateContentOutcome> RecordConflictAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Note note, string content, string hash, long baseRevId, Guid authorId, string deviceId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Unlike the fast path (where baseRevId is guaranteed to equal a real HeadRevId we just
        // read), baseRevId reaching here can be arbitrary client input - a stale/malicious
        // client, a typo, or even a real revision id that belongs to a *different* note. Inserted
        // straight into Revision.ParentRevId without checking, a nonexistent value trips the FK
        // constraint as an unhandled DbUpdateException (500). Reject it as a client error instead:
        // a 400 rather than 409, because this isn't a legitimate sync conflict with something to
        // resolve against - the baseRevId doesn't name anything real for this note at all.
        var baseRevisionExists = await _db.Revisions
            .AnyAsync(r => r.Id == baseRevId && r.NoteId == note.Id, cancellationToken);
        if (!baseRevisionExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateContentOutcome.Fail(400, "invalid_base_rev", "baseRevId does not reference a revision of this note.");
        }

        var revision = new Revision
        {
            VaultId = note.VaultId,
            NoteId = note.Id,
            ParentRevId = baseRevId,
            AuthorId = authorId,
            DeviceId = deviceId,
            Kind = RevisionKind.Edit,
            Path = note.Path,
            ContentHash = hash,
            IsConflict = true,
            CreatedAt = now,
        };
        _db.Revisions.Add(revision);
        await _db.SaveChangesAsync(cancellationToken);

        note.HasConflict = true;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _storage.WriteConflictBlobAsync(note.VaultId, revision.Id, content, cancellationToken, precomputedHash: hash);
        }
        catch (IOException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex,
                "Failed to write conflict blob for note {NoteId} revision {RevisionId}; DB changes rolled back.",
                note.Id, revision.Id);
            return UpdateContentOutcome.Fail(500, "io_error", "Failed to store conflicting content.");
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "MANUAL RECONCILIATION REQUIRED for note {NoteId}: conflict commit failed after the conflict blob " +
                "for revision {RevisionId} was already written to disk.",
                note.Id, revision.Id);
            return UpdateContentOutcome.Fail(500, "io_error", "Failed to persist conflict.");
        }

        var headRevId = note.HeadRevId!.Value;
        await BroadcastAsync(note.VaultId, note.Id, revision.Id, "edit", note.Path, null, hash, deviceId, true, now, cancellationToken);
        return UpdateContentOutcome.Conflict(headRevId, revision.Id);
    }

    public async Task<NoteOperationResult> RenameAsync(
        Guid noteId, string? rawNewPath, Guid authorId, string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawNewPath))
        {
            return NoteOperationResult.Fail(400, "invalid_request", "newPath is required.");
        }

        var note = await _db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return NoteOperationResult.Fail(404, "not_found", "No such note.");
        }

        string newPath;
        try
        {
            newPath = _storage.SafePath(rawNewPath);
        }
        catch (VaultPathException ex)
        {
            return NoteOperationResult.Fail(400, "invalid_path", ex.Message);
        }

        if (!newPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return NoteOperationResult.Fail(400, "invalid_path", "Note paths must end with '.md'.");
        }

        if (newPath == note.Path)
        {
            return NoteOperationResult.Fail(400, "invalid_request", "newPath is the same as the current path.");
        }

        if (await _db.Notes.AnyAsync(n => n.Id != note.Id && n.VaultId == note.VaultId && n.Path == newPath && !n.IsDeleted, cancellationToken))
        {
            return NoteOperationResult.Fail(409, "note_exists", "A note already exists at that path.");
        }

        if (VaultPathCollision.HasCaseOnlyCollision(_storage, note.VaultId, newPath, excludePrefix: note.Path))
        {
            return NoteOperationResult.Fail(409, "case_conflict",
                "A file or folder with the same name (different case) already exists.");
        }

        string currentContent;
        try
        {
            currentContent = await _storage.ReadNoteAsync(note.VaultId, note.Path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            currentContent = string.Empty;
        }

        var oldPath = note.Path;
        var fallbackTitle = System.IO.Path.GetFileNameWithoutExtension(newPath);
        var extracted = MarkdownIndexer.Extract(currentContent, fallbackTitle);
        var plainText = MarkdownIndexer.StripToPlainText(currentContent);
        var now = DateTimeOffset.UtcNow;

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var revision = new Revision
            {
                VaultId = note.VaultId,
                NoteId = note.Id,
                ParentRevId = note.HeadRevId,
                AuthorId = authorId,
                DeviceId = deviceId,
                Kind = RevisionKind.Rename,
                Path = newPath,
                OldPath = oldPath,
                ContentHash = note.ContentHash,
                IsConflict = false,
                CreatedAt = now,
            };
            _db.Revisions.Add(revision);
            await _db.SaveChangesAsync(cancellationToken);

            note.HeadRevId = revision.Id;
            note.Path = newPath;
            note.Title = extracted.Title;
            note.UpdatedAt = now;

            await UpdateSearchVectorAsync(note.Id, note.Title, plainText, cancellationToken);
            await ResolveIncomingLinksAsync(note.VaultId, note, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _storage.MoveAsync(note.VaultId, oldPath, newPath, cancellationToken);
            }
            catch (VaultConflictException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NoteOperationResult.Fail(409, "note_conflict", "Something already exists at the destination path.");
            }
            catch (IOException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex,
                    "Failed to move note '{OldPath}' -> '{NewPath}' in vault {VaultId}; DB changes rolled back.",
                    oldPath, newPath, note.VaultId);
                return NoteOperationResult.Fail(500, "io_error", "Failed to move note on disk.");
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    await _storage.MoveAsync(note.VaultId, newPath, oldPath, cancellationToken);
                    _logger.LogError(ex,
                        "Note rename commit failed for note {NoteId} ('{OldPath}' -> '{NewPath}'); " +
                        "compensating move-back succeeded.", note.Id, oldPath, newPath);
                }
                catch (Exception compensationEx)
                {
                    _logger.LogCritical(compensationEx,
                        "MANUAL RECONCILIATION REQUIRED for note {NoteId}: rename commit failed " +
                        "('{OldPath}' -> '{NewPath}', error: {CommitError}) AND the compensating move-back also failed.",
                        note.Id, oldPath, newPath, ex.Message);
                }

                return NoteOperationResult.Fail(500, "io_error", "Failed to persist note rename.");
            }
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        await BroadcastAsync(note.VaultId, note.Id, note.HeadRevId!.Value, "rename", newPath, oldPath, note.ContentHash, deviceId, false, now, cancellationToken);
        return NoteOperationResult.Ok(ToMetaDto(note));
    }

    /// <summary>
    /// Soft-delete-first, matching TreeController.DeleteFolder: the DB row is flipped and the
    /// revision committed as one transaction, and only then is anything removed from disk
    /// (best-effort - a failure there just orphans the file, which is benign since nothing reads a
    /// soft-deleted note's content through the DB anymore).
    /// </summary>
    public async Task<NoteOperationResult> DeleteAsync(Guid noteId, Guid authorId, string deviceId, CancellationToken cancellationToken)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && !n.IsDeleted, cancellationToken);
        if (note is null)
        {
            return NoteOperationResult.Fail(404, "not_found", "No such note.");
        }

        var now = DateTimeOffset.UtcNow;

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            var revision = new Revision
            {
                VaultId = note.VaultId,
                NoteId = note.Id,
                ParentRevId = note.HeadRevId,
                AuthorId = authorId,
                DeviceId = deviceId,
                Kind = RevisionKind.Delete,
                Path = note.Path,
                ContentHash = note.ContentHash,
                IsConflict = false,
                CreatedAt = now,
            };
            _db.Revisions.Add(revision);
            await _db.SaveChangesAsync(cancellationToken);

            note.IsDeleted = true;
            note.HeadRevId = revision.Id;
            note.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            await _storage.DeleteAsync(note.VaultId, note.Path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete on-disk file for soft-deleted note '{NotePath}' in vault {VaultId}; " +
                "file is orphaned and can be cleaned up later.", note.Path, note.VaultId);
        }

        await BroadcastAsync(note.VaultId, note.Id, note.HeadRevId!.Value, "delete", note.Path, null, note.ContentHash, deviceId, false, now, cancellationToken);
        return NoteOperationResult.Ok(ToMetaDto(note));
    }

    /// <summary>Recomputes a note's tags and outgoing links from freshly extracted content, resolving link targets against the vault's current notes.</summary>
    private async Task ReindexNoteAsync(Note note, MarkdownIndexResult extracted, CancellationToken cancellationToken)
    {
        var existingNoteTags = await _db.NoteTags.Where(nt => nt.NoteId == note.Id).ToListAsync(cancellationToken);
        _db.NoteTags.RemoveRange(existingNoteTags);

        foreach (var tagName in extracted.Tags)
        {
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.VaultId == note.VaultId && t.Name == tagName, cancellationToken);
            if (tag is null)
            {
                tag = new Tag { Id = Guid.NewGuid(), VaultId = note.VaultId, Name = tagName };
                _db.Tags.Add(tag);
            }

            _db.NoteTags.Add(new NoteTag { NoteId = note.Id, Tag = tag });
        }

        var existingLinks = await _db.Links.Where(l => l.SourceNoteId == note.Id).ToListAsync(cancellationToken);
        _db.Links.RemoveRange(existingLinks);

        if (extracted.Links.Count > 0)
        {
            var candidates = await _db.Notes
                .Where(n => n.VaultId == note.VaultId && !n.IsDeleted && n.Id != note.Id)
                .Select(n => new { n.Id, n.Path })
                .ToListAsync(cancellationToken);

            foreach (var link in extracted.Links)
            {
                var targetId = candidates.FirstOrDefault(c => LinkResolver.Matches(link.Target, c.Path))?.Id;
                _db.Links.Add(new Link
                {
                    Id = Guid.NewGuid(),
                    SourceNoteId = note.Id,
                    TargetNoteId = targetId,
                    TargetText = link.Target,
                });
            }
        }
    }

    /// <summary>
    /// Obsidian-style late resolution: when a note is created or renamed, any vault-wide link whose
    /// target text now matches this note's (new) path is pointed at it, so links written before the
    /// target existed (or before it had this name) become resolved without the source note being
    /// re-saved.
    /// </summary>
    private async Task ResolveIncomingLinksAsync(Guid vaultId, Note note, CancellationToken cancellationToken)
    {
        var unresolved = await _db.Links
            .Where(l => l.TargetNoteId == null && l.SourceNote!.VaultId == vaultId && !l.SourceNote.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var link in unresolved)
        {
            if (LinkResolver.Matches(link.TargetText, note.Path))
            {
                link.TargetNoteId = note.Id;
            }
        }
    }

    /// <summary>
    /// Updates the note's search_vector via Postgres' <c>to_tsvector</c> (title weighted 'A', body
    /// 'B' so title matches rank higher) - computed server-side rather than in C# so stemming/stop-word
    /// handling matches whatever the same Postgres text-search config uses at query time.
    /// </summary>
    private async Task UpdateSearchVectorAsync(Guid noteId, string title, string plainText, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE notes SET search_vector =
                 setweight(to_tsvector('english', {title}), 'A') ||
                 setweight(to_tsvector('english', {plainText}), 'B')
             WHERE id = {noteId}
             """,
            cancellationToken);
    }

    private Task BroadcastAsync(
        Guid vaultId, Guid? noteId, long seq, string kind, string path, string? oldPath,
        string contentHash, string deviceId, bool isConflict, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        _broadcaster.BroadcastAsync(
            new RevisionBroadcast(vaultId, noteId, seq, kind, path, oldPath, contentHash, deviceId, isConflict, createdAt),
            cancellationToken);

    private static NoteMetaDto ToMetaDto(Note note) =>
        new(note.Id, note.Path, note.Title, note.HasConflict, note.SizeBytes, note.HeadRevId, note.UpdatedAt);
}
