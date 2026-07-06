namespace Slate.Server.Domain;

/// <summary>A tag scoped to a vault. Table: tags.</summary>
public class Tag
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public required string Name { get; set; }

    public Vault? Vault { get; set; }
    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
}
