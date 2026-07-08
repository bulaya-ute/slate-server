using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slate.Server.Auth;
using Slate.Server.Domain;
using Slate.Server.Vaults;

namespace Slate.Server.Search;

[ApiController]
[Route("api/vaults/{v:guid}")]
[Authorize]
public class SearchController : SlateControllerBase
{
    private readonly SearchService _search;

    public SearchController(SearchService search)
    {
        _search = search;
    }

    [HttpGet("search")]
    [RequireVaultAccess(VaultAccessLevel.Read)]
    public async Task<IActionResult> Search(Guid v, [FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "q is required.");
        }

        var results = await _search.SearchAsync(v, q, cancellationToken);
        return Ok(results);
    }
}
