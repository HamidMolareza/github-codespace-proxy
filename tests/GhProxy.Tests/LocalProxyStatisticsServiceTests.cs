using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class LocalProxyStatisticsServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsHourlyBucketsAndGitHubMismatchForLast24Hours()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(db, account.Id, now.AddHours(-8), now.AddHours(-7).AddMinutes(-30));
            db.CodespaceStateSamples.Add(new CodespaceStateSample
            {
                AccountId = account.Id,
                CodespaceName = "space",
                State = "Available",
                ObservedAt = now.AddHours(-2),
                Source = "sync"
            });
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal("24h", stats.Period);
            Assert.Equal(24, stats.HourlyBuckets.Count);
            Assert.Empty(stats.DailyBuckets);
            Assert.Equal(1800, stats.Totals.ActiveSeconds);
            Assert.Equal(1, stats.Totals.SessionCount);
            Assert.Single(stats.Mismatches);
            Assert.Contains(stats.HourlyBuckets, x => x.ActiveSeconds == 1800);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_MergesOverlappingSessionsBeforeCountingTotalActiveTime()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(db, account.Id, now.AddHours(-6), now.AddHours(-5));
            await AddSessionAsync(db, account.Id, now.AddHours(-5).AddMinutes(-30), now.AddHours(-4));
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal(7200, stats.Totals.ActiveSeconds);
            Assert.Equal(2, stats.Totals.SessionCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsDailyBucketsForSevenDayPeriod()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(db, account.Id, now.AddDays(-1), now.AddDays(-1).AddHours(2));
            await AddSessionAsync(db, account.Id, now.AddDays(-2), now.AddDays(-2).AddHours(1));
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("7d", CancellationToken.None);

            Assert.Equal("7d", stats.Period);
            Assert.Empty(stats.HourlyBuckets);
            Assert.Equal(7, stats.DailyBuckets.Count);
            Assert.Equal(10800, stats.Totals.ActiveSeconds);
            Assert.Equal(10800d / 7, stats.Totals.AverageActiveSecondsPerDay);
            Assert.Equal(2, stats.DailyBuckets.Count(x => x.ActiveSeconds > 0));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_CountsRetryDowntimeAsErrorAndExcludesItFromActiveTime()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(db, account.Id, now.AddHours(-4), now.AddHours(-1));
            AddEvent(db, "local_proxy.start.failed", now.AddHours(-3));
            AddEvent(db, "local_proxy.xray.started", now.AddHours(-2));
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal(7200, stats.Totals.ActiveSeconds);
            Assert.Equal(3600, stats.Totals.ErrorSeconds);
            Assert.Equal(3600, stats.HourlyBuckets.Sum(x => x.ErrorSeconds));
            Assert.All(stats.HourlyBuckets, bucket => Assert.True(bucket.ActiveSeconds + bucket.ErrorSeconds + bucket.OffSeconds <= 3600));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_ExtendsUnresolvedFailureToCurrentTime()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            AddEvent(db, "local_proxy.start.failed", now.AddMinutes(-90));
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal(0, stats.Totals.ActiveSeconds);
            Assert.Equal(5400, stats.Totals.ErrorSeconds);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_CountsErrorSessionsAsErrorTime()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(
                db,
                account.Id,
                now.AddHours(-5),
                now.AddHours(-4),
                LocalProxySessionStatus.Error,
                "failed to start");
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal(0, stats.Totals.ActiveSeconds);
            Assert.Equal(3600, stats.Totals.ErrorSeconds);
            Assert.Equal(1, stats.Totals.SessionCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_DoesNotExtendLegacyTerminalSessionsWithoutStoppedAtToNow()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = await AddAccountAsync(db);
            await AddSessionAsync(
                db,
                account.Id,
                now.AddHours(-6),
                null,
                LocalProxySessionStatus.Error,
                "legacy failed startup",
                now.AddHours(-6).AddMinutes(5));
            await AddSessionAsync(db, account.Id, now.AddHours(-2), now.AddHours(-1));
            await AddSessionAsync(db, account.Id, now.AddMinutes(-30), null, LocalProxySessionStatus.Running);
            await db.SaveChangesAsync();
            var service = new LocalProxyStatisticsService(db, new TestClock(now));

            var stats = await service.GetAsync("24h", CancellationToken.None);

            Assert.Equal(5400, stats.Totals.ActiveSeconds);
            Assert.Equal(300, stats.Totals.ErrorSeconds);
            Assert.Equal(LocalProxySessionStatus.Running.ToString(), stats.Sessions[0].Status);
            Assert.Equal(LocalProxySessionStatus.Stopped.ToString(), stats.Sessions[1].Status);
            Assert.Equal(LocalProxySessionStatus.Error.ToString(), stats.Sessions[2].Status);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static async Task<GitHubAccount> AddAccountAsync(AppDbContext db)
    {
        var account = new GitHubAccount
        {
            DisplayName = "Primary",
            Username = "octocat",
            ProtectedPersonalAccessToken = "token",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.GitHubAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task AddSessionAsync(
        AppDbContext db,
        Guid accountId,
        DateTimeOffset startedAt,
        DateTimeOffset? stoppedAt,
        LocalProxySessionStatus status = LocalProxySessionStatus.Stopped,
        string? lastError = null,
        DateTimeOffset? lastActivityAt = null)
    {
        var profile = await db.LocalProxyProfiles.FirstOrDefaultAsync();
        if (profile is null)
        {
            profile = new LocalProxyProfile
            {
                Name = "Default",
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                SocksPort = 8910,
                IdleShutdownMinutes = 30,
                CreatedAt = startedAt.AddMinutes(-1),
                UpdatedAt = startedAt.AddMinutes(-1)
            };
            db.LocalProxyProfiles.Add(profile);
            await db.SaveChangesAsync();
        }

        db.LocalProxySessions.Add(new LocalProxySession
        {
            ProfileId = profile.Id,
            Status = status,
            BindHost = profile.BindHost,
            LocalPort = profile.LocalPort,
            SocksPort = profile.SocksPort,
            StartedAt = startedAt,
            LastActivityAt = lastActivityAt ?? stoppedAt ?? startedAt,
            LastRequestAt = lastActivityAt ?? stoppedAt,
            StoppedAt = stoppedAt,
            LastError = lastError,
            AccountId = accountId,
            CodespaceName = "space",
            RemoteProxyPort = 8899
        });
        await db.SaveChangesAsync();
    }

    private static void AddEvent(AppDbContext db, string eventType, DateTimeOffset timestamp)
    {
        db.OperationalEvents.Add(new OperationalEvent
        {
            EventType = eventType,
            Severity = eventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Error" : "Information",
            Message = eventType,
            Timestamp = timestamp
        });
    }

    private static AppDbContext CreateDb(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new AppDbContext(options);
    }

    private static void DeleteDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
