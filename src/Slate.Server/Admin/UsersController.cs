using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Common;
using Slate.Server.Data;
using Slate.Server.Domain;

namespace Slate.Server.Admin;

/// <summary>Admin-only user management: create, list, edit (role/disabled/password reset), delete.</summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = "AdminOnly")]
public class UsersController : SlateControllerBase
{
    private readonly SlateDbContext _db;
    private readonly IPasswordHasher _passwordHasher;

    public UsersController(SlateDbContext db, IPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var users = await _db.Users.OrderBy(u => u.Username).ToListAsync(cancellationToken);
        return Ok(users.Select(UserDto.FromEntity));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
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

        var role = UserRole.User;
        if (!string.IsNullOrWhiteSpace(request.Role) && !Enum.TryParse(request.Role, ignoreCase: true, out role))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_role", "role must be 'admin' or 'user'.");
        }

        if (await _db.Users.AnyAsync(u => u.Username == request.Username, cancellationToken))
        {
            return Error(StatusCodes.Status409Conflict, "username_taken", "That username is already in use.");
        }

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = role,
            IsDisabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, UserDto.FromEntity(user));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user is null)
        {
            return Error(StatusCodes.Status404NotFound, "user_not_found", "No such user.");
        }

        if (request.Role is not null)
        {
            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_role", "role must be 'admin' or 'user'.");
            }

            user.Role = role;
        }

        if (request.IsDisabled is not null)
        {
            user.IsDisabled = request.IsDisabled.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (request.NewPassword.Length < PasswordPolicy.MinimumLength)
            {
                return Error(StatusCodes.Status400BadRequest, "weak_password",
                    $"Password must be at least {PasswordPolicy.MinimumLength} characters.");
            }

            user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(UserDto.FromEntity(user));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user is null)
        {
            return Error(StatusCodes.Status404NotFound, "user_not_found", "No such user.");
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }
}
