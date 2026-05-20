using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class GitHubCodespaceMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<GitHubOptions> options,
    IClock clock,
    ILogger<GitHubCodespaceMaintenanceService> logger) : BackgroundService
{
    private readonly GitHubOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.SyncIntervalSeconds, 60, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GitHub Codespaces maintenance failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
        var events = scope.ServiceProvider.GetRequiredService<IOperationalEventSink>();
        var accountIds = await db.GitHubAccounts
            .AsNoTracking()
            .Where(x => x.ValidationStatus != GitHubAccountValidationStatus.Invalid)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var accountId in accountIds)
        {
            IReadOnlyList<CodespaceSnapshot> snapshots;
            try
            {
                snapshots = await service.SyncAsync(accountId, cancellationToken);
            }
            catch (Exception ex)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "github.maintenance.sync.failed",
                    OperationalEventSeverity.Warning,
                    ex.Message,
                    NodeId: accountId,
                    StandardError: ex.ToString()), cancellationToken);
                continue;
            }

            foreach (var snapshot in snapshots.Where(ShouldStop))
            {
                try
                {
                    await service.StopAsync(accountId, snapshot.Name, cancellationToken);
                    await events.WriteAsync(new OperationalEventWrite(
                        "github.maintenance.autostop",
                        OperationalEventSeverity.Information,
                        $"Stopped idle Codespace {snapshot.Name}.",
                        NodeId: accountId,
                        Details: new { snapshot.Name, snapshot.LastUsedAt }), cancellationToken);
                }
                catch (Exception ex)
                {
                    await events.WriteAsync(new OperationalEventWrite(
                        "github.maintenance.autostop.failed",
                        OperationalEventSeverity.Warning,
                        ex.Message,
                        NodeId: accountId,
                        StandardError: ex.ToString(),
                        Details: new { snapshot.Name }), cancellationToken);
                }
            }
        }
    }

    private bool ShouldStop(CodespaceSnapshot snapshot)
    {
        if (!string.Equals(snapshot.State, "Available", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lastUsed = snapshot.LastUsedAt ?? snapshot.UpdatedAt ?? snapshot.CreatedAt;
        if (lastUsed is null)
        {
            return false;
        }

        return clock.UtcNow - lastUsed.Value >= TimeSpan.FromMinutes(Math.Max(5, _options.AutoStopIdleMinutes));
    }
}
