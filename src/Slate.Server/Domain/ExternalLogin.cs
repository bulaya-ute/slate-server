namespace Slate.Server.Domain;

/// <summary>Stub for future OIDC/external-login linkage. Table: external_logins. Unused in v1.</summary>
public class ExternalLogin
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Provider { get; set; }
    public required string Subject { get; set; }

    public User? User { get; set; }
}
