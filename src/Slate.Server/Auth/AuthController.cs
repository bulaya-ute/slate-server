using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Common;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Auth;

/// <summary>
/// Login/refresh/logout/me/register. There is no public, invite-less registration: accounts
/// exist only via admin creation (see Admin/UsersController) or a one-time admin-issued invite
/// (see Admin/InvitesController + POST register below).
/// </summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IRefreshTokenService _refreshTokenService;

    public AuthController(
        SlateDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IRefreshTokenService refreshTokenService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _refreshTokenService = refreshTokenService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "username and password are required.");
        }

        var user = await _db.Users.SingleOrDefaultAsync(u => u.Username == request.Username, cancellationToken);
        if (user is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid username or password.");
        }

        if (user.IsDisabled)
        {
            return Error(StatusCodes.Status401Unauthorized, "account_disabled", "This account has been disabled.");
        }

        var accessToken = _jwtTokenService.CreateAccessToken(user);
        var refreshToken = await _refreshTokenService.IssueAsync(user.Id, cancellationToken: cancellationToken);

        return Ok(new AuthResponse(accessToken, refreshToken, UserDto.FromEntity(user)));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "refreshToken is required.");
        }

        var result = await _refreshTokenService.RotateAsync(request.RefreshToken, cancellationToken);

        return result.Outcome switch
        {
            RefreshRotationOutcome.Success => Ok(new RefreshResponse(
                _jwtTokenService.CreateAccessToken(result.User!),
                result.NewPlainToken!)),
            RefreshRotationOutcome.Reused => Error(
                StatusCodes.Status401Unauthorized, "refresh_token_reused",
                "This refresh token was already used; the session has been revoked for safety."),
            RefreshRotationOutcome.Expired => Error(
                StatusCodes.Status401Unauthorized, "refresh_token_expired", "Refresh token has expired."),
            RefreshRotationOutcome.UserDisabled => Error(
                StatusCodes.Status401Unauthorized, "account_disabled", "This account has been disabled."),
            _ => Error(StatusCodes.Status401Unauthorized, "invalid_refresh_token", "Refresh token is invalid."),
        };
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await _refreshTokenService.RevokeAsync(request.RefreshToken, CurrentUserId, cancellationToken);
        }

        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await _db.Users.FindAsync(new object[] { CurrentUserId }, cancellationToken);
        if (user is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_token", "The authenticated user no longer exists.");
        }

        return Ok(UserDto.FromEntity(user));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InviteToken)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request",
                "inviteToken, username, password, and displayName are required.");
        }

        if (request.Password.Length < PasswordPolicy.MinimumLength)
        {
            return Error(StatusCodes.Status400BadRequest, "weak_password",
                $"Password must be at least {PasswordPolicy.MinimumLength} characters.");
        }

        var tokenHash = TokenHasher.Hash(request.InviteToken);
        var invite = await _db.Invites.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
        if (invite is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_invite", "Invite token is invalid.");
        }

        if (invite.UsedAt is not null || invite.UsedBy is not null)
        {
            return Error(StatusCodes.Status410Gone, "invite_already_used", "This invite has already been used.");
        }

        if (invite.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Error(StatusCodes.Status410Gone, "invite_expired", "This invite has expired.");
        }

        if (await _db.Users.AnyAsync(u => u.Username == request.Username, cancellationToken))
        {
            return Error(StatusCodes.Status409Conflict, "username_taken", "That username is already in use.");
        }

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = invite.Role,
            IsDisabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(user);

        invite.UsedAt = now;
        invite.UsedBy = user.Id;

        await _db.SaveChangesAsync(cancellationToken);

        var accessToken = _jwtTokenService.CreateAccessToken(user);
        var refreshToken = await _refreshTokenService.IssueAsync(user.Id, cancellationToken: cancellationToken);

        return Ok(new AuthResponse(accessToken, refreshToken, UserDto.FromEntity(user)));
    }
}
