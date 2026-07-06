namespace Slate.Server.Domain;

/// <summary>
/// An append-only change record. Table: revisions. The bigserial <see cref="Id"/> doubles as the
/// global per-vault change sequence: clients page catch-up with WHERE vault_id = @v AND id > @since.
/// </summary>
public class Revision
{
    public long Id { get; set; }
    public Guid VaultId { get; set; }

    /// <summary>Null for revisions not tied to a note row (e.g. folder-only or attachment operations).</summary>
    public Guid? NoteId { get; set; }

    public long? ParentRevId { get; set; }

    /// <summary>Null when the change originated outside the app (a direct filesystem edit picked up by the watcher).</summary>
    public Guid? AuthorId { get; set; }

    public required string DeviceId { get; set; }
    public RevisionKind Kind { get; set; }
    public required string Path { get; set; }
    public string? OldPath { get; set; }
    public required string ContentHash { get; set; }
    public bool IsConflict { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Vault? Vault { get; set; }
    public Note? Note { get; set; }
    public User? Author { get; set; }
    public Revision? ParentRevision { get; set; }
}
