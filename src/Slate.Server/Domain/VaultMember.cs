namespace Slate.Server.Domain;

/// <summary>A user's membership/access level on a vault. Table: vault_members. Composite key (vault_id, user_id).</summary>
public class VaultMember
{
    public Guid VaultId { get; set; }
    public Guid UserId { get; set; }
    public VaultAccessLevel Access { get; set; } = VaultAccessLevel.Read;

    public Vault? Vault { get; set; }
    public User? User { get; set; }
}
