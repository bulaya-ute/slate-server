using System.Net;
using System.Security.Cryptography;
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

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

    /// <summary>
    /// Individual proxy IPs trusted to set X-Forwarded-For/X-Forwarded-Proto, resolved from the
    /// comma-separated SLATE_KNOWN_PROXIES env var (entries without a "/" prefix length). See
    /// <see cref="KnownProxyNetworks"/> for the CIDR half and the "unset" default.
    /// </summary>
    public required IReadOnlyList<IPAddress> KnownProxies { get; init; }

    /// <summary>
    /// CIDR ranges trusted to set X-Forwarded-For/X-Forwarded-Proto, resolved from the
    /// comma-separated SLATE_KNOWN_PROXIES env var (entries with a "/" prefix length). When that
    /// var is unset entirely, defaults to loopback + the RFC1918 private ranges - the
    /// docker-compose/reverse-proxy-on-same-host deployment this app documents - and never wider,
    /// so forwarded headers from arbitrary public sources are never trusted by default.
    /// </summary>
    public required IReadOnlyList<AspNetIPNetwork> KnownProxyNetworks { get; init; }

    public static SlateOptions FromConfiguration(IConfiguration configuration)
    {
        var dataDir = Resolve(configuration, "SLATE_DATA_DIR", "Slate:DataDir", "./.slate-data");
        var dbConnection = Resolve(configuration, "SLATE_DB_CONNECTION", "Slate:DbConnection");
        var serverName = Resolve(configuration, "SLATE_SERVER_NAME", "Slate:ServerName", "Slate");
        var jwtSecret = ResolveJwtSecret(configuration, dataDir);
        var authRateLimitPerMinute = ResolveAuthRateLimitPerMinute(configuration);
        var (knownProxies, knownProxyNetworks) = ResolveKnownProxies(configuration);

        return new SlateOptions
        {
            DbConnection = dbConnection,
            DataDir = dataDir,
            JwtSecret = jwtSecret,
            ServerName = serverName,
            AuthRateLimitPerMinute = authRateLimitPerMinute,
            KnownProxies = knownProxies,
            KnownProxyNetworks = knownProxyNetworks,
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

    /// <summary>
    /// Parses SLATE_KNOWN_PROXIES (comma-separated IPs and/or CIDR ranges, e.g.
    /// "203.0.113.5,10.20.0.0/16") into the two lists ForwardedHeadersOptions wants. Left unset,
    /// the deployment is presumed to match the documented model - a reverse proxy (Caddy/Traefik)
    /// on the same host or a sibling container on a private Docker network - so the default trusts
    /// loopback plus the standard RFC1918 private ranges (10/8, 172.16/12, 192.168/16) and nothing
    /// beyond that. Forwarded headers from any other source are never trusted unless explicitly
    /// configured.
    /// </summary>
    private static (IReadOnlyList<IPAddress> Proxies, IReadOnlyList<AspNetIPNetwork> Networks) ResolveKnownProxies(
        IConfiguration configuration)
    {
        var raw = configuration["SLATE_KNOWN_PROXIES"] ?? configuration["Slate:KnownProxies"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (
                new[] { IPAddress.Loopback, IPAddress.IPv6Loopback },
                new[]
                {
                    new AspNetIPNetwork(IPAddress.Parse("127.0.0.0"), 8),
                    new AspNetIPNetwork(IPAddress.Parse("10.0.0.0"), 8),
                    new AspNetIPNetwork(IPAddress.Parse("172.16.0.0"), 12),
                    new AspNetIPNetwork(IPAddress.Parse("192.168.0.0"), 16),
                });
        }

        var proxies = new List<IPAddress>();
        var networks = new List<AspNetIPNetwork>();

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slashIndex = entry.IndexOf('/');
            if (slashIndex < 0)
            {
                proxies.Add(IPAddress.Parse(entry));
                continue;
            }

            var address = IPAddress.Parse(entry[..slashIndex]);
            var prefixLength = int.Parse(entry[(slashIndex + 1)..]);
            networks.Add(new AspNetIPNetwork(address, prefixLength));
        }

        return (proxies, networks);
    }
}
