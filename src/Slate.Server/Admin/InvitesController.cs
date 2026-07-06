using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Admin;

/// <summary>
/// Admin-only invite management. Invites are the only way anyone other than an admin ends up
/// with a Slate account (no open registration): an admin issues a one-time link, whoever holds
/// it calls POST /api/auth/register with the token to create their account.
/// </summary>
[ApiController]
[Route("api/invites")]
[Authorize(Policy = "AdminOnly")]
public class InvitesController : SlateControllerBase
{
    private const int DefaultExpiresInHours = 24 * 7;

    private readonly SlateDbContext _db;

    public InvitesController(SlateDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var invites = await _db.Invites.OrderByDescending(i => i.ExpiresAt).ToListAsync(cancellationToken);
        return Ok(invites.Select(InviteDto.FromEntity));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateInviteRequest request, CancellationToken cancellationToken)
    {
        var role = UserRole.User;
        if (!string.IsNullOrWhiteSpace(request.Role) && !Enum.TryParse(request.Role, ignoreCase: true, out role))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_role", "role must be 'admin' or 'user'.");
        }

        // Admin-only endpoint, so the requested value is trusted as-is (including <= 0, which is
        // useful for immediately-expiring/testing invites); only a missing value gets the default.
        var expiresInHours = request.ExpiresInHours ?? DefaultExpiresInHours;
        var plainToken = OpaqueTokenGenerator.Generate();

        var invite = new Invite
        {
            Id = Guid.NewGuid(),
            TokenHash = TokenHasher.Hash(plainToken),
            CreatedBy = CurrentUserId,
            Role = role,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(expiresInHours),
        };

        _db.Invites.Add(invite);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created,
            new InviteCreatedResponse(plainToken, invite.ExpiresAt, role.ToString().ToLowerInvariant()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var invite = await _db.Invites.FindAsync(new object[] { id }, cancellationToken);
        if (invite is null)
        {
            return Error(StatusCodes.Status404NotFound, "invite_not_found", "No such invite.");
        }

        _db.Invites.Remove(invite);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }
}
