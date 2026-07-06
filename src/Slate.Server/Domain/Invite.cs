namespace Slate.Server.Domain;

/// <summary>A one-time invite link an admin can issue. Table: invites.</summary>
public class Invite
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public Guid CreatedBy { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? UsedBy { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    public User? CreatedByUser { get; set; }
    public User? UsedByUser { get; set; }
}
