using Slate.Server.Domain;

namespace Slate.Server.Auth;

/// <summary>Wire shape for a user, shared by /auth/* and /users/* responses.</summary>
public record UserDto(Guid Id, string Username, string DisplayName, string Role, bool IsDisabled)
{
    public static UserDto FromEntity(User user) =>
        new(user.Id, user.Username, user.DisplayName, user.Role.ToString().ToLowerInvariant(), user.IsDisabled);
}
