namespace Slate.Server.Domain;

/// <summary>A non-markdown file stored in a vault. Table: attachments.</summary>
public class Attachment
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public required string Path { get; set; }
    public required string ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public required string Mime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Vault? Vault { get; set; }
}
