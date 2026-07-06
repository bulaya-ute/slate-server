namespace Slate.Server.Domain;

/// <summary>A note vault (a folder tree of markdown files). Table: vaults.</summary>
public class Vault
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User? Owner { get; set; }
    public ICollection<VaultMember> Members { get; set; } = new List<VaultMember>();
    public ICollection<Note> Notes { get; set; } = new List<Note>();
    public ICollection<Revision> Revisions { get; set; } = new List<Revision>();
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
}
