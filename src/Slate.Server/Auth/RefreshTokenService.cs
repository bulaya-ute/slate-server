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
            .AsNoTracking()
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
            await RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
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

        // Two concurrent rotations of the same still-valid token can both pass every check above
        // before either commits (read-check-then-write TOCTOU), which would mint two successors
        // from what must be a single-use token without ever tripping reuse detection. Close it by
        // making the actual rotation an atomic conditional UPDATE - only the request that flips
        // RevokedAt from null to non-null may mint a successor - and keep that UPDATE plus the
        // successor insert in one transaction. Postgres holds the row lock acquired by the UPDATE
        // for the whole transaction (not just the statement), so a concurrent loser's identical
        // conditional UPDATE blocks until this transaction fully commits (successor included)
        // before it re-evaluates RevokedAt and observes 0 rows affected - guaranteeing the loser's
        // family-revoke below always runs after, and therefore always catches, the winner's new
        // successor token too.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _db.RefreshTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            await RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
            return RefreshRotationResult.Reused();
        }

        var newPlain = IssueWithinFamily(token.UserId, token.FamilyId, token.DeviceName);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RefreshRotationResult.Success(token.User, newPlain);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);
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
