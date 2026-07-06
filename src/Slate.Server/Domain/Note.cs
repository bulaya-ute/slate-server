using NpgsqlTypes;

namespace Slate.Server.Domain;

/// <summary>
/// Metadata for a note. The markdown body itself lives on disk (see IVaultStorage);
/// Postgres never stores note content. Table: notes.
/// </summary>
public class Note
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }

    /// <summary>Vault-relative path, forward-slash, no leading slash. Unique per vault among non-deleted notes.</summary>
    public required string Path { get; set; }

    public required string Title { get; set; }
    public required string ContentHash { get; set; }

    /// <summary>The current head revision. Nullable because the Note row is created before its first Revision row exists.</summary>
    public long? HeadRevId { get; set; }

    public long SizeBytes { get; set; }
    public bool HasConflict { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Full-text search vector, maintained by the indexer. Backed by a GIN index.</summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Vault? Vault { get; set; }
    public Revision? HeadRevision { get; set; }
    public ICollection<Revision> Revisions { get; set; } = new List<Revision>();
    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
    public ICollection<Link> OutgoingLinks { get; set; } = new List<Link>();
    public ICollection<Link> IncomingLinks { get; set; } = new List<Link>();
}
