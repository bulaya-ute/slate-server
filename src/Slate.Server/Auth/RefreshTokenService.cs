using Microsoft.EntityFrameworkCore;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Auth;

public class RefreshTokenService : IRefreshTokenService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    private readonly SlateDbContext _db;

    public RefreshTokenService(SlateDbContext db)
    {
        _db = db;
    }

    public async Task<string> IssueAsync(Guid userId, string? deviceName = null, CancellationToken cancellationToken = default)
    {
        var plain = IssueWithinFamily(userId, Guid.NewGuid(), deviceName);
        await _db.SaveChangesAsync(cancellationToken);
        return plain;
    }

    public async Task<RefreshRotationResult> RotateAsync(string plainToken, CancellationToken cancellationToken = default)
    {
        var hash = TokenHasher.Hash(plainToken);
        var token = await _db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null)
        {
            return RefreshRotationResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        if (token.RevokedAt is not null)
        {
            // Replaying an already-rotated token: someone else may hold the current, valid
            // successor (token theft). Revoke the whole family so both parties are logged out.
            await _db.RefreshTokens
                .Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);
            return RefreshRotationResult.Reused();
        }

        if (token.ExpiresAt <= now)
        {
            return RefreshRotationResult.Expired();
        }

        if (token.User is null || token.User.IsDisabled)
        {
            return RefreshRotationResult.UserDisabled();
        }

        token.RevokedAt = now;
        var newPlain = IssueWithinFamily(token.UserId, token.FamilyId, token.DeviceName);
        await _db.SaveChangesAsync(cancellationToken);

        return RefreshRotationResult.Success(token.User, newPlain);
    }

    public async Task RevokeAsync(string plainToken, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var hash = TokenHasher.Hash(plainToken);
        var token = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || token.UserId != ownerUserId || token.RevokedAt is not null)
        {
            return;
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private string IssueWithinFamily(Guid userId, Guid familyId, string? deviceName)
    {
        var plain = OpaqueTokenGenerator.Generate();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = TokenHasher.Hash(plain),
            DeviceName = deviceName,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime),
            RevokedAt = null,
        });

        return plain;
    }
}
