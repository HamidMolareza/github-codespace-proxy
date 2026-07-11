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
                WHERE type = 'table' AND name IN ('OperationalEvents', 'GitHubAccounts', 'CodespaceSnapshots', 'CodespaceStateSamples', 'LocalProxyProfiles', 'LocalProxySessions', 'LocalProxyGatewayRequests')
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Equal(["CodespaceSnapshots", "CodespaceStateSamples", "GitHubAccounts", "LocalProxyGatewayRequests", "LocalProxyProfiles", "LocalProxySessions", "OperationalEvents"], tables);
            var localProxySessionColumns = await db.Database.SqlQueryRaw<string>(
                """
                SELECT name AS Value
                FROM pragma_table_info('LocalProxySessions')
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Contains("LastRequestAt", localProxySessionColumns);
            Assert.Contains("AccountId", localProxySessionColumns);
            Assert.Contains("CodespaceName", localProxySessionColumns);
            Assert.Contains("RemoteProxyPort", localProxySessionColumns);
            var stateSampleIndexes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT name AS Value
                FROM sqlite_master
                WHERE type = 'index' AND tbl_name = 'CodespaceStateSamples'
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Contains("IX_CodespaceStateSamples_ObservedAt", stateSampleIndexes);
            var gatewayRequestIndexes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT name AS Value
                FROM sqlite_master
                WHERE type = 'index' AND tbl_name = 'LocalProxyGatewayRequests'
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Contains("IX_LocalProxyGatewayRequests_ObservedAt", gatewayRequestIndexes);
            Assert.Contains("IX_LocalProxyGatewayRequests_SessionId", gatewayRequestIndexes);
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
                LastActivityAt = DateTimeOffset.UtcNow,
                AccountId = Guid.NewGuid(),
                CodespaceName = "space",
                RemoteProxyPort = 8899
            });
            await db.SaveChangesAsync();

            await initializer.InitializeAsync(CancellationToken.None);
            await db.Entry(profile).ReloadAsync();

            Assert.Equal(GhProxy.Api.Domain.LocalProxyProfileStatus.Stopped, profile.Status);
            var session = db.LocalProxySessions.AsNoTracking().Single();
            Assert.Equal(GhProxy.Api.Domain.LocalProxySessionStatus.Error, session.Status);
            Assert.Equal(DatabaseSchemaInitializer.RestartedActiveSessionMessage, session.LastError);
            Assert.Equal("space", session.CodespaceName);
            Assert.Equal(8899, session.RemoteProxyPort);
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
    public async Task InitializeAsync_BackfillsSortableOperationalEventTimestamp()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            var initializer = new DatabaseSchemaInitializer(db);
            await initializer.InitializeAsync(CancellationToken.None);
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_OperationalEvents_TimestampUtcMs\";");
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_OperationalEvents_EventType_TimestampUtcMs\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"OperationalEvents\" DROP COLUMN \"TimestampUtcMs\";");
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX "IX_OperationalEvents_Timestamp" ON "OperationalEvents" ("Timestamp");
                INSERT INTO "OperationalEvents" ("Id", "Timestamp", "Severity", "EventType", "Message", "TimedOut")
                VALUES ('11111111-1111-1111-1111-111111111111', '2026-07-10 18:30:00.0000000+00:00', 'Information', 'test', 'legacy', 0);
                """);

            await initializer.InitializeAsync(CancellationToken.None);

            var utcMs = await db.Database.SqlQueryRaw<long>(
                "SELECT \"TimestampUtcMs\" AS Value FROM \"OperationalEvents\" LIMIT 1")
                .SingleAsync();
            var indexes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT name AS Value
                FROM sqlite_master
                WHERE type = 'index' AND tbl_name = 'OperationalEvents'
                ORDER BY name
                """)
                .ToListAsync();
            Assert.Equal(new DateTimeOffset(2026, 7, 10, 18, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), utcMs);
            Assert.Contains("IX_OperationalEvents_TimestampUtcMs", indexes);
            Assert.Contains("IX_OperationalEvents_EventType_TimestampUtcMs", indexes);
            Assert.DoesNotContain("IX_OperationalEvents_Timestamp", indexes);
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
