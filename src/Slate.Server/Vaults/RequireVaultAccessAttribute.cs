using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Common;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Vaults;

/// <summary>
/// Reusable action filter enforcing vault membership + a minimum access level. Expects a Guid
/// route parameter named "v" (every "/api/vaults/{v}/..." route in this API uses that name).
///
/// Deliberately returns 404 - not 403 - when the caller isn't a member, so an unauthorized caller
/// can't distinguish "no such vault" from "this vault exists but you're not on it" (don't leak
/// existence). A member whose access level is too low for the action gets 403 instead, since they
/// already know the vault exists.
///
/// Global Admins bypass the membership check entirely and are treated as Owner-level on every
/// vault, matching the "owner/admin only" wording used for vault rename/delete in the API contract.
///
/// On success, the resolved access level is stashed in HttpContext.Items[HttpContextItemKey]
/// (Owner for the admin-bypass case) so actions can read it back without a second query.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequireVaultAccessAttribute : Attribute, IAsyncActionFilter
{
    public const string HttpContextItemKey = "VaultAccess";
    private const string RouteParamName = "v";

    private readonly VaultAccessLevel _minimum;

    public RequireVaultAccessAttribute(VaultAccessLevel minimum)
    {
        _minimum = minimum;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        if (!context.RouteData.Values.TryGetValue(RouteParamName, out var rawVaultId)
            || !Guid.TryParse(rawVaultId?.ToString(), out var vaultId))
        {
            context.Result = Problem(StatusCodes.Status400BadRequest, "invalid_request", "Missing or invalid vault id.");
            return;
        }

        var userIdClaim = http.User.FindFirst("sub")?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            context.Result = Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Authentication required.");
            return;
        }

        var db = http.RequestServices.GetRequiredService<SlateDbContext>();

        if (!await db.Vaults.AnyAsync(v => v.Id == vaultId))
        {
            context.Result = NotFound();
            return;
        }

        if (http.User.IsInRole("admin"))
        {
            http.Items[HttpContextItemKey] = VaultAccessLevel.Owner;
            await next();
            return;
        }

        var member = await db.VaultMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.VaultId == vaultId && m.UserId == userId);

        if (member is null)
        {
            context.Result = NotFound();
            return;
        }

        if (!VaultAccess.Satisfies(member.Access, _minimum))
        {
            context.Result = Problem(StatusCodes.Status403Forbidden, "forbidden",
                "You do not have permission to perform this action.");
            return;
        }

        http.Items[HttpContextItemKey] = member.Access;
        await next();
    }

    private static ObjectResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "not_found", "The requested resource was not found.");

    private static ObjectResult Problem(int statusCode, string code, string message) =>
        new(new ErrorEnvelope(code, message)) { StatusCode = statusCode };
}
