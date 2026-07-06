using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Common;
using Slate.Server.Configuration;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly SlateOptions _options;
    private readonly IPasswordHasher _passwordHasher;

    public SystemController(SlateDbContext db, SlateOptions options, IPasswordHasher passwordHasher)
    {
        _db = db;
        _options = options;
        _passwordHasher = passwordHasher;
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

    /// <summary>
    /// Anonymous, first-run only: creates the first admin account. Permanently 410 once any user
    /// exists - there is no open registration, so this is the only anonymous account-creation path.
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup(SetupRequest request, CancellationToken cancellationToken)
    {
        if (await _db.Users.AnyAsync(cancellationToken))
        {
            return Error(StatusCodes.Status410Gone, "setup_already_completed",
                "Initial setup has already been completed.");
        }

        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request",
                "username, password, and displayName are required.");
        }

        if (request.Password.Length < PasswordPolicy.MinimumLength)
        {
            return Error(StatusCodes.Status400BadRequest, "weak_password",
                $"Password must be at least {PasswordPolicy.MinimumLength} characters.");
        }

        var now = DateTimeOffset.UtcNow;
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = UserRole.Admin,
            IsDisabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }
}

public record SetupRequest(string? Username, string? Password, string? DisplayName);

public class SystemInfoResponse
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required int ApiVersion { get; init; }
    public required string ServerName { get; init; }
    public required bool SetupRequired { get; init; }
}
