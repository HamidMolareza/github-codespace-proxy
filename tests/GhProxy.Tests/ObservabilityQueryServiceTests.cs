using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Tests;

public sealed class ObservabilityQueryServiceTests
{
    [Fact]
    public async Task Queries_FilterAndAggregateInRetentionWindow()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await db.Database.EnsureCreatedAsync();
            for (var index = 0; index < 1000; index++)
            {
                db.OperationalEvents.Add(new OperationalEvent
                {
                    Timestamp = now.AddDays(-20).AddSeconds(index),
                    Severity = OperationalEventSeverity.Information,
                    EventType = "expired",
                    Message = "old"
                });
            }
            db.OperationalEvents.AddRange(
                new OperationalEvent
                {
                    Timestamp = now.AddMinutes(-3),
                    Severity = OperationalEventSeverity.Warning,
                    EventType = "recent.warning",
                    Message = "needle warning"
                },
                new OperationalEvent
                {
                    Timestamp = now.AddMinutes(-2),
                    Severity = OperationalEventSeverity.Error,
                    EventType = "recent.error",
                    Message = "needle error",
                    CommandKind = "probe",
                    ExitCode = 1,
                    DurationMs = 30
                },
                new OperationalEvent
                {
                    Timestamp = now.AddMinutes(-1),
                    Severity = OperationalEventSeverity.Information,
                    EventType = "recent.ok",
                    Message = "ok",
                    CommandKind = "probe",
                    ExitCode = 0,
                    DurationMs = 10
                });
            await db.SaveChangesAsync();
            var service = new ObservabilityQueryService(
                db,
                new TestClock(now),
                Options.Create(new ObservabilityOptions { RetentionDays = 14 }));

            var latest = await service.GetActivityAsync(null, null, null, null, null, null, 2, CancellationToken.None);
            var filtered = await service.GetActivityAsync(null, null, null, null, null, "needle", 100, CancellationToken.None);
            var summary = await service.GetSummaryAsync(CancellationToken.None);

            Assert.Equal(["recent.ok", "recent.error"], latest.Select(x => x.EventType));
            Assert.Equal(2, filtered.Count);
            Assert.Equal(3, summary.RecentCount);
            Assert.Equal(1, summary.ErrorCount);
            Assert.Equal(1, summary.WarningCount);
            Assert.Equal(1, summary.CommandFailureCount);
            Assert.Equal(20, summary.AverageCommandDurationMs);
            Assert.Equal("recent.error", summary.LastError?.EventType);
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

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
