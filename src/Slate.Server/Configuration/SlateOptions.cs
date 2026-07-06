using System.Security.Cryptography;

namespace Slate.Server.Configuration;

/// <summary>
/// Resolved server configuration. Each value is read with env-var-first precedence
/// (SLATE_DB_CONNECTION, SLATE_DATA_DIR, SLATE_JWT_SECRET, SLATE_SERVER_NAME), falling back to
/// the "Slate" section of appsettings for local dev, per the deployment spec.
/// </summary>
public class SlateOptions
{
    public required string DbConnection { get; init; }
    public required string DataDir { get; init; }
    public required string JwtSecret { get; init; }
    public required string ServerName { get; init; }

    /// <summary>
    /// Fixed-window permit limit applied per client IP to /api/auth/* (design spec: ~10/min).
    /// Overridable so integration tests can raise it well above what the functional test suite
    /// itself calls in a one-minute window, while a dedicated rate-limit test lowers it instead.
    /// </summary>
    public required int AuthRateLimitPerMinute { get; init; }

    public static SlateOptions FromConfiguration(IConfiguration configuration)
    {
        var dataDir = Resolve(configuration, "SLATE_DATA_DIR", "Slate:DataDir", "./.slate-data");
        var dbConnection = Resolve(configuration, "SLATE_DB_CONNECTION", "Slate:DbConnection");
        var serverName = Resolve(configuration, "SLATE_SERVER_NAME", "Slate:ServerName", "Slate");
        var jwtSecret = ResolveJwtSecret(configuration, dataDir);
        var authRateLimitPerMinute = ResolveAuthRateLimitPerMinute(configuration);

        return new SlateOptions
        {
            DbConnection = dbConnection,
            DataDir = dataDir,
            JwtSecret = jwtSecret,
            ServerName = serverName,
            AuthRateLimitPerMinute = authRateLimitPerMinute,
        };
    }

    private static string Resolve(IConfiguration configuration, string envVar, string appSettingsKey, string? fallback = null)
    {
        var value = configuration[envVar];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = configuration[appSettingsKey];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new InvalidOperationException(
            $"Missing required configuration. Set the {envVar} environment variable or the '{appSettingsKey}' appsettings key.");
    }

    /// <summary>
    /// SLATE_JWT_SECRET, if unset, is auto-generated and persisted under the data dir so it
    /// survives restarts (matches the deployment spec: "auto-generated to data dir if unset").
    /// </summary>
    private static string ResolveJwtSecret(IConfiguration configuration, string dataDir)
    {
        var value = configuration["SLATE_JWT_SECRET"];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = configuration["Slate:JwtSecret"];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        Directory.CreateDirectory(dataDir);
        var secretPath = Path.Combine(dataDir, "jwt-secret");

        if (File.Exists(secretPath))
        {
            var existing = File.ReadAllText(secretPath).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        File.WriteAllText(secretPath, generated);
        return generated;
    }

    private static int ResolveAuthRateLimitPerMinute(IConfiguration configuration)
    {
        var raw = configuration["SLATE_AUTH_RATE_LIMIT_PER_MINUTE"] ?? configuration["Slate:AuthRateLimitPerMinute"];
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 10;
    }
}
