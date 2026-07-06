namespace Slate.Server.Auth;

/// <summary>Issues, rotates, and revokes refresh tokens (30-day, rotating, hashed at rest).</summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a brand-new token, starting a new rotation family.</summary>
    Task<string> IssueAsync(Guid userId, string? deviceName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the presented token and, on success, revokes it and issues its replacement
    /// (same family). If the token was already revoked (i.e. it's being replayed after having
    /// already been rotated), the entire family is revoked and <see cref="RefreshRotationOutcome.Reused"/>
    /// is returned.
    /// </summary>
    Task<RefreshRotationResult> RotateAsync(string plainToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes a single token (logout). No-op if it doesn't exist or belongs to another user.</summary>
    Task RevokeAsync(string plainToken, Guid ownerUserId, CancellationToken cancellationToken = default);
}
