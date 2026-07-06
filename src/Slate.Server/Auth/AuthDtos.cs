namespace Slate.Server.Auth;

public record LoginRequest(string? Username, string? Password);

public record RefreshRequest(string? RefreshToken);

public record LogoutRequest(string? RefreshToken);

public record RegisterRequest(string? InviteToken, string? Username, string? Password, string? DisplayName);

/// <summary>Login-shape response, also returned by /auth/register on success.</summary>
public record AuthResponse(string AccessToken, string RefreshToken, UserDto User);

public record RefreshResponse(string AccessToken, string RefreshToken);
