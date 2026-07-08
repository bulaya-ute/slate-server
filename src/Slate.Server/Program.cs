using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Slate.Server.Auth;
using Slate.Server.Common;
using Slate.Server.Configuration;
using Slate.Server.Data;
using Slate.Server.Notes;
using Slate.Server.Search;
using Slate.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

// Resolved lazily via DI (not read off builder.Configuration directly) so that test overrides
// registered through WebApplicationFactory.ConfigureWebHost/ConfigureAppConfiguration - which are
// only merged in when the host finishes building - are honored. Env vars (SLATE_DB_CONNECTION,
// SLATE_DATA_DIR, SLATE_JWT_SECRET, SLATE_SERVER_NAME) take precedence, falling back to
// appsettings for local dev.
builder.Services.AddSingleton(sp => SlateOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddDbContext<SlateDbContext>((sp, options) =>
    options
        .UseNpgsql(sp.GetRequiredService<SlateOptions>().DbConnection)
        .UseSnakeCaseNamingConvention());

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

// Singleton: the write-marker registry (RegisterWrite/WasOurWrite, for S5's watcher echo
// suppression) is in-memory, process-wide state that must survive across requests/scopes.
builder.Services.AddSingleton<IVaultStorage, VaultStorage>();

// No-op until S5 wires up the real SignalR-hub-backed broadcaster (see IRevisionBroadcaster docs) -
// NoteService and friends call it unconditionally so that swap is a one-line DI change later.
builder.Services.AddSingleton<IRevisionBroadcaster, NullRevisionBroadcaster>();
builder.Services.AddScoped<NoteService>();
builder.Services.AddScoped<SearchService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Bound against SlateOptions via DI (rather than read off builder.Configuration directly) for the
// same test-override reasons as the DbContext registration above.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SlateOptions>((options, slateOptions) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtTokenService.ConfigureValidationParameters(slateOptions);

        // GET /api/vaults/{v}/files/{**path} is used directly in <img> src etc., where the caller
        // can't set an Authorization header - so (and only so) it also accepts the JWT via
        // ?access_token=. Scoped to that one route by path shape (".../files/...") rather than
        // applied to every request, so no other endpoint's auth model changes.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token)
                    && context.Request.Path.StartsWithSegments("/api/vaults", out var remainder)
                    && remainder.Value is { } path
                    && path.Contains("/files/", StringComparison.OrdinalIgnoreCase))
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        context.Token = accessToken;
                    }
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

// The rate limiter below (and anything else that reads Connection.RemoteIpAddress) needs the
// real client IP, not the reverse proxy's, whenever the app sits behind one (the documented
// Caddy/Traefik deployment). ForwardedHeadersMiddleware rewrites RemoteIpAddress/Request.Scheme
// from X-Forwarded-For/X-Forwarded-Proto - but only when the *immediate* connection is from a
// KnownProxies/KnownNetworks entry, so an internet client can't just forge the header to spoof
// an arbitrary source IP. KnownProxies/KnownNetworks are resolved via SlateOptions (SLATE_KNOWN_PROXIES)
// for the same test-override reasons as the other options above.
builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<SlateOptions>((options, slateOptions) =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        foreach (var proxy in slateOptions.KnownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in slateOptions.KnownProxyNetworks)
        {
            options.KnownNetworks.Add(network);
        }
    });

// Fixed-window rate limit on /api/auth/* (design spec: ~10/min/IP), partitioned by client IP.
// The permit limit is configurable (SLATE_AUTH_RATE_LIMIT_PER_MINUTE) so the integration test
// suite can raise it well above anything the functional tests themselves call in one minute,
// while a dedicated test lowers it to prove the throttling actually works.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
    {
        var slateOptions = httpContext.RequestServices.GetRequiredService<SlateOptions>();
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = slateOptions.AuthRateLimitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
});

var app = builder.Build();

// Must run before anything that reads Request.Scheme or Connection.RemoteIpAddress - including
// UseHttpsRedirection below and the rate limiter's partition-key selector - so both see the real
// client, not the reverse proxy, when SLATE_KNOWN_PROXIES (or its private-network default; see
// SlateOptions) trusts the immediate connection.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Rewrites otherwise-empty error responses (401/403 from the auth middleware, 404 from routing,
// 429 from the rate limiter, ...) into the API's {error:{code,message}} envelope. Controller
// actions that already write their own envelope (see SlateControllerBase.Error) already set a
// body/content-type, so this only fires for the framework-generated empty ones.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    response.ContentType = "application/json";

    var (code, message) = response.StatusCode switch
    {
        StatusCodes.Status401Unauthorized => ("unauthorized", "Authentication required."),
        StatusCodes.Status403Forbidden => ("forbidden", "You do not have permission to perform this action."),
        StatusCodes.Status404NotFound => ("not_found", "The requested resource was not found."),
        StatusCodes.Status405MethodNotAllowed => ("method_not_allowed", "Method not allowed."),
        StatusCodes.Status429TooManyRequests => ("rate_limited", "Too many requests. Try again later."),
        _ => ("error", "An error occurred."),
    };

    await response.WriteAsJsonAsync(new ErrorEnvelope(code, message));
});

// Skipped under "Testing" (WebApplicationFactory integration tests): TestServer requests are
// always plain HTTP with no configurable HTTPS port, so this middleware only ever logs a
// "Failed to determine the https port" warning there - pure noise in otherwise-pristine test
// output, and not a real redirect since a real client would never hit it.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> (integration tests) can bind to this entry point.
public partial class Program { }
