using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Slate.Server.Configuration;
using Slate.Server.Domain;

namespace Slate.Server.Auth;

/// <summary>
/// Creates and (via <see cref="ConfigureValidationParameters"/>) validates the JWT access tokens
/// described in the design spec: 15 minute lifetime, `sub` + `role` claims, HMAC-SHA256 signed
/// with SLATE_JWT_SECRET.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    public const string Issuer = "slate-server";
    public const string Audience = "slate-clients";
    public const string RoleClaimType = "role";

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly SlateOptions _options;

    public JwtTokenService(SlateOptions options)
    {
        _options = options;
    }

    public string CreateAccessToken(User user)
    {
        var now = DateTime.UtcNow;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(RoleClaimType, user.Role.ToString().ToLowerInvariant()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(AccessTokenLifetime),
            signingCredentials: new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static TokenValidationParameters ConfigureValidationParameters(SlateOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(options),
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = RoleClaimType,
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };

    private static SymmetricSecurityKey SigningKey(SlateOptions options) =>
        new(Encoding.UTF8.GetBytes(options.JwtSecret));
}
