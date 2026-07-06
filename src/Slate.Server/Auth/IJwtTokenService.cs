using Slate.Server.Domain;

namespace Slate.Server.Auth;

/// <summary>Issues short-lived JWT access tokens.</summary>
public interface IJwtTokenService
{
    /// <summary>Creates a signed access token carrying `sub` (user id) and `role` claims.</summary>
    string CreateAccessToken(User user);
}
