namespace Slate.Server.Domain;

/// <summary>A Slate account. Table: users.</summary>
public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<ExternalLogin> ExternalLogins { get; set; } = new List<ExternalLogin>();
    public ICollection<Vault> OwnedVaults { get; set; } = new List<Vault>();
    public ICollection<VaultMember> VaultMemberships { get; set; } = new List<VaultMember>();
}
