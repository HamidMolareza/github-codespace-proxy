using GhProxy.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class ObservabilityRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<ObservabilityOptions> options,
    ILogger<ObservabilityRetentionService> logger)
    : BackgroundService
{
    private readonly ObservabilityOptions _options = options.Value;
    private static readonly string[] StatisticsEventTypes =
    [
        "codespace_proxy.start.failed_before_runtime",
        "local_proxy.start.failed",
        "local_proxy.port.unavailable",
        "local_proxy.startup_recovery.failed",
        "local_proxy.probe.http.failure",
        "local_proxy.probe.socks.failure",
        "local_proxy.xray.exited",
        "codespace_proxy.tunnel.interrupted",
        "codespace_proxy.tunnel.exited",
        "local_proxy.xray.started",
        "local_proxy.stop.completed",
        "local_proxy.stop.none",
        "local_proxy.idle.timeout",
        "codespace_proxy.tunnel.reconnected",
        "session.stop.completed",
        "session.idle.timeout"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CleanupAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (_options.RetentionDays <= 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var statisticsCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(_options.RetentionDays, _options.StatisticsRetentionDays));
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var deletedOld = await db.OperationalEvents
                .Where(x => x.TimestampUtcMs < statisticsCutoff.ToUnixTimeMilliseconds())
                .ExecuteDeleteAsync(cancellationToken);
            var deletedDetailed = await db.OperationalEvents
                .Where(x =>
                    x.TimestampUtcMs < cutoff.ToUnixTimeMilliseconds() &&
                    !StatisticsEventTypes.Contains(x.EventType))
                .ExecuteDeleteAsync(cancellationToken);
            if (deletedOld + deletedDetailed > 0)
            {
                logger.LogInformation("Deleted {Count} expired operational events.", deletedOld + deletedDetailed);
            }
            var deletedAudits = await db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "AuditLogs" WHERE julianday("Timestamp") < julianday({0});""",
                [cutoff.UtcDateTime.ToString("O")],
                cancellationToken);
            if (deletedAudits > 0)
            {
                logger.LogInformation("Deleted {Count} expired audit events.", deletedAudits);
            }
        }

        if (!_options.EnableJsonlFile || !Directory.Exists(_options.LogDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_options.LogDirectory, "operational-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    logger.LogInformation("Deleted expired operational log file {File}.", file);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean operational log file {File}.", file);
            }
        }
    }
}
