using GhProxy.Api.Data;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class DatabaseSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesOperationalTablesIdempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            var initializer = new DatabaseSchemaInitializer(db);

            await initializer.InitializeAsync(CancellationToken.None);
            await initializer.InitializeAsync(CancellationToken.None);

            var tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT name AS Value
                FROM sqlite_master
                WHERE type = 'table' AND name IN ('OperationalEvents', 'GitHubAccounts', 'CodespaceSnapshots', 'LocalProxyProfiles', 'LocalProxySessions')
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Equal(["CodespaceSnapshots", "GitHubAccounts", "LocalProxyProfiles", "LocalProxySessions", "OperationalEvents"], tables);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_ResetsStaleLocalProxyRuntimeState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            var initializer = new DatabaseSchemaInitializer(db);
            await initializer.InitializeAsync(CancellationToken.None);
            var profile = new GhProxy.Api.Domain.LocalProxyProfile
            {
                Name = "stale",
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                IdleShutdownMinutes = 30,
                Status = GhProxy.Api.Domain.LocalProxyProfileStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.LocalProxyProfiles.Add(profile);
            db.LocalProxySessions.Add(new GhProxy.Api.Domain.LocalProxySession
            {
                ProfileId = profile.Id,
                Status = GhProxy.Api.Domain.LocalProxySessionStatus.Running,
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                StartedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await initializer.InitializeAsync(CancellationToken.None);
            await db.Entry(profile).ReloadAsync();

            Assert.Equal(GhProxy.Api.Domain.LocalProxyProfileStatus.Stopped, profile.Status);
            Assert.Equal(GhProxy.Api.Domain.LocalProxySessionStatus.Error, db.LocalProxySessions.AsNoTracking().Single().Status);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static AppDbContext CreateDb(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new AppDbContext(options);
    }
}
