using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Vaults;

/// <summary>
/// Manual vault-membership check for endpoints keyed by note/resource id rather than a "v" vault-id
/// route parameter (e.g. <c>/api/notes/{id}/content</c>, <c>/api/notes/{id}/backlinks</c>), where
/// <see cref="RequireVaultAccessAttribute"/> can't apply directly. Mirrors that attribute's
/// semantics exactly - callers resolve the vault id themselves (typically from a note lookup) and
/// then call <see cref="TryGetAccessAsync"/>: a null result means "treat as 404, not 403" (don't
/// leak existence to a non-member), matching the attribute's documented behavior. Global Admins
/// bypass membership entirely and are treated as Owner-level, also matching the attribute.
/// </summary>
public static class VaultMembership
{
    public static async Task<VaultAccessLevel?> TryGetAccessAsync(SlateDbContext db, ClaimsPrincipal user, Guid vaultId)
    {
        if (user.IsInRole("admin"))
        {
            return VaultAccessLevel.Owner;
        }

        var userIdClaim = user.FindFirst("sub")?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return null;
        }

        var member = await db.VaultMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.VaultId == vaultId && m.UserId == userId);

        return member?.Access;
    }
}
