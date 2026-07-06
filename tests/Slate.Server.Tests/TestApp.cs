using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Slate.Server.Data;
using Testcontainers.PostgreSql;

namespace Slate.Server.Tests;

/// <summary>
/// Boots the real Slate.Server app (via WebApplicationFactory) against a Testcontainers-managed
/// Postgres instance, with EF Core migrations applied before any test runs.
///
/// Shared for the whole test session via <see cref="SlateTestCollection"/> (an xUnit collection
/// fixture): the Postgres container starts once and is reused by every test class in the
/// collection. Later tasks should add their test classes to that same collection rather than
/// standing up their own container.
/// </summary>
public class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("slate_test")
        .WithUsername("slate")
        .WithPassword("slate")
        .Build();

    /// <summary>A fresh temp directory standing in for SLATE_DATA_DIR for the life of the test session.</summary>
    public string DataDir { get; } = Directory.CreateTempSubdirectory("slate-test-data-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SLATE_DB_CONNECTION"] = _dbContainer.GetConnectionString(),
                ["SLATE_DATA_DIR"] = DataDir,
                ["SLATE_JWT_SECRET"] = "test-only-jwt-secret-0123456789-not-for-production",
                ["SLATE_SERVER_NAME"] = "Slate Test Server",
                // The real default (~10/min/IP) would blow past what this shared, in-process test
                // server sees across a whole test session (every request shares one loopback
                // partition key). A dedicated test overrides this back down via
                // WithWebHostBuilder to actually exercise throttling in isolation.
                ["SLATE_AUTH_RATE_LIMIT_PER_MINUTE"] = "100000",
            });
        });
    }

    /// <summary>
    /// Truncates every mapped table (auto-discovered from the EF model, so it stays correct as
    /// later tasks add tables) and restarts identity sequences. Test classes call this from their
    /// own IAsyncLifetime.InitializeAsync so each test method starts from a clean, empty database
    /// regardless of what earlier tests in the shared session left behind.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();

        var tableNames = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(name => name is not null)
            .Distinct();

        var quotedNames = string.Join(", ", tableNames.Select(name => $"\"{name}\""));
        // Table names come from our own EF model (never user input), so building this string
        // ourselves is safe; identifiers can't be passed as SQL parameters anyway.
        string sql = "TRUNCATE TABLE " + quotedNames + " RESTART IDENTITY CASCADE;";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        // Accessing Services builds the host (triggers ConfigureWebHost above), which is why the
        // container must already be started: GetConnectionString() needs the mapped port.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlateDbContext>();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await base.DisposeAsync();

        if (Directory.Exists(DataDir))
        {
            Directory.Delete(DataDir, recursive: true);
        }
    }
}
