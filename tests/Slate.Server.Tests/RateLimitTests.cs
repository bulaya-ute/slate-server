using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Slate.Server.Tests;

/// <summary>
/// Exercises the real /api/auth/* rate limiter. Uses WithWebHostBuilder to spin up a second,
/// independent WebApplicationFactory (own DI container, own in-memory rate-limiter state) that
/// reuses the already-running Postgres container but overrides SLATE_AUTH_RATE_LIMIT_PER_MINUTE
/// down to a tiny value - the shared TestApp instance runs with a very high limit (see TestApp)
/// so the rest of the functional suite is never throttled.
/// </summary>
[Collection(SlateTestCollection.Name)]
public class RateLimitTests
{
    private readonly TestApp _app;

    public RateLimitTests(TestApp app)
    {
        _app = app;
    }

    [Fact]
    public async Task AuthEndpoints_ExceedingTheFixedWindowLimit_Returns429WithErrorEnvelope()
    {
        const int limit = 3;

        using var limitedFactory = _app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SLATE_AUTH_RATE_LIMIT_PER_MINUTE"] = limit.ToString(),
                });
            });
        });

        var client = limitedFactory.CreateClient();

        for (var i = 0; i < limit; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var throttled = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var body = await throttled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", body.GetProperty("error").GetProperty("code").GetString());
    }

    // Behind the documented Caddy/Traefik reverse-proxy deployment, every real client shares the
    // proxy's IP at the TCP connection level, so partitioning on Connection.RemoteIpAddress alone
    // (pre-fix) would put every client in a single global bucket. TestServer's requests come from
    // loopback, which is trusted by default (see SlateOptions.ResolveKnownProxies), so
    // ForwardedHeadersMiddleware rewrites RemoteIpAddress from X-Forwarded-For before the rate
    // limiter ever sees it - proving two distinct forwarded client IPs get two distinct buckets.
    [Fact]
    public async Task AuthEndpoints_PartitionByForwardedClientIp_WhenBehindAKnownProxy()
    {
        const int limit = 3;

        using var limitedFactory = _app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SLATE_AUTH_RATE_LIMIT_PER_MINUTE"] = limit.ToString(),
                });
            });
        });

        var clientA = limitedFactory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        var clientB = limitedFactory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");

        for (var i = 0; i < limit; i++)
        {
            var response = await clientA.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var throttledA = await clientA.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledA.StatusCode);

        // Client B, forwarded from the same (trusted, loopback) proxy but with a different
        // client IP, must not be affected by client A having exhausted its window.
        var responseB = await clientB.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, responseB.StatusCode);
    }

    [Fact]
    public async Task NonAuthEndpoints_AreNotRateLimited()
    {
        const int limit = 2;

        using var limitedFactory = _app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SLATE_AUTH_RATE_LIMIT_PER_MINUTE"] = limit.ToString(),
                });
            });
        });

        var client = limitedFactory.CreateClient();

        for (var i = 0; i < limit + 3; i++)
        {
            var response = await client.GetAsync("/api/system/info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
