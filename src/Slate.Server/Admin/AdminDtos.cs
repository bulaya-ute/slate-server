namespace Slate.Server.Admin;

public record CreateUserRequest(string? Username, string? Password, string? DisplayName, string? Role);

public record UpdateUserRequest(string? Role, bool? IsDisabled, string? NewPassword);

public record CreateInviteRequest(string? Role, int? ExpiresInHours);

/// <summary>Returned once, at creation time - the plaintext token is never retrievable again.</summary>
public record InviteCreatedResponse(string Token, DateTimeOffset ExpiresAt, string Role);

public record InviteDto(Guid Id, string Role, DateTimeOffset ExpiresAt, Guid CreatedBy, Guid? UsedBy, DateTimeOffset? UsedAt)
{
    public static InviteDto FromEntity(Domain.Invite invite) => new(
        invite.Id,
        invite.Role.ToString().ToLowerInvariant(),
        invite.ExpiresAt,
        invite.CreatedBy,
        invite.UsedBy,
        invite.UsedAt);
}
