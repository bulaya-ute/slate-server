using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Configuration;
using Slate.Server.Data;

namespace Slate.Server.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly SlateDbContext _db;
    private readonly SlateOptions _options;

    public SystemController(SlateDbContext db, SlateOptions options)
    {
        _db = db;
        _options = options;
    }

    /// <summary>Anonymous. Lets clients validate compatibility and discover whether first-run setup is needed.</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public async Task<ActionResult<SystemInfoResponse>> GetInfo(CancellationToken cancellationToken)
    {
        var setupRequired = !await _db.Users.AnyAsync(cancellationToken);

        return Ok(new SystemInfoResponse
        {
            Name = "Slate",
            Version = "0.1.0",
            ApiVersion = 1,
            ServerName = _options.ServerName,
            SetupRequired = setupRequired,
        });
    }
}

public class SystemInfoResponse
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required int ApiVersion { get; init; }
    public required string ServerName { get; init; }
    public required bool SetupRequired { get; init; }
}
