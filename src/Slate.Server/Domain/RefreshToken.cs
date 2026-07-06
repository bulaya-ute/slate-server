namespace Slate.Server.Domain;

/// <summary>A rotating refresh token issued to a device. Table: refresh_tokens.</summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }

    /// <summary>
    /// Shared by every token descended from the same login via rotation. Lets the server revoke
    /// an entire rotation chain in one query when a already-rotated (revoked) token is replayed,
    /// per the refresh-token-reuse-detection requirement.
    /// </summary>
    public Guid FamilyId { get; set; }

    public string? DeviceName { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public User? User { get; set; }
}
