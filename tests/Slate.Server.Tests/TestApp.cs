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
            });
        });
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
