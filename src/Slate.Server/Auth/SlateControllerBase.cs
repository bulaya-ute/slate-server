using Microsoft.AspNetCore.Mvc;
using Slate.Server.Common;

namespace Slate.Server.Auth;

/// <summary>Shared helpers for controllers that read the authenticated user's identity/claims.</summary>
public abstract class SlateControllerBase : ControllerBase
{
    /// <summary>The `sub` claim of the caller's access token, as set by <see cref="JwtTokenService"/>.</summary>
    protected Guid CurrentUserId => Guid.Parse(User.FindFirst("sub")?.Value
        ?? throw new InvalidOperationException("Authenticated request is missing the 'sub' claim."));

    protected static ObjectResult Error(int statusCode, string code, string message) =>
        new(new ErrorEnvelope(code, message)) { StatusCode = statusCode };
}
