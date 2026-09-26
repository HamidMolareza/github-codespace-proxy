using GhProxy.Api.Contracts;
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
    private readonly HashSet<Guid> _reportedActiveSessions = [];

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

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
        var events = scope.ServiceProvider.GetRequiredService<IOperationalEventSink>();
        var activeSessionIds = await db.LocalProxySessions
            .AsNoTracking()
            .Where(x => x.Status == LocalProxySessionStatus.Starting || x.Status == LocalProxySessionStatus.Running)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        _reportedActiveSessions.IntersectWith(activeSessionIds);
        var accountIds = await db.GitHubAccounts
            .AsNoTracking()
            .Where(x => x.ValidationStatus != GitHubAccountValidationStatus.Invalid)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var accountId in accountIds)
        {
            IReadOnlyList<CodespaceSnapshot> snapshots;
            GitHubUsageResponse? usage = null;
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

            try
            {
                usage = await service.GetUsageAsync(accountId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "github.maintenance.usage.failed",
                    OperationalEventSeverity.Warning,
                    ex.Message,
                    NodeId: accountId,
                    StandardError: ex.ToString()), cancellationToken);
            }

            if (usage is not null)
            {
                var account = await db.GitHubAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
                if (account is not null)
                {
                    var cleanup = scope.ServiceProvider.GetRequiredService<CodespaceStorageCleanupService>();
                    snapshots = (await cleanup.CleanupAsync(account, usage, snapshots, cancellationToken)).Snapshots;
                }
            }

            var activeProxySessions = await db.LocalProxySessions
                .AsNoTracking()
                .Where(x => x.AccountId == accountId)
                .Where(x => x.Status == LocalProxySessionStatus.Starting || x.Status == LocalProxySessionStatus.Running)
                .Where(x => x.CodespaceName != null && x.CodespaceName != "")
                .Select(x => new { x.Id, x.CodespaceName })
                .ToListAsync(cancellationToken);

            foreach (var snapshot in snapshots.Where(ShouldStop))
            {
                var activeSession = activeProxySessions.FirstOrDefault(x =>
                    string.Equals(x.CodespaceName, snapshot.Name, StringComparison.OrdinalIgnoreCase));
                if (activeSession is not null)
                {
                    if (_reportedActiveSessions.Add(activeSession.Id))
                    {
                        await events.WriteAsync(new OperationalEventWrite(
                            "github.maintenance.autostop.skipped.active_proxy",
                            OperationalEventSeverity.Information,
                            $"Skipped idle auto-stop for Codespace {snapshot.Name} because its local proxy session is active.",
                            NodeId: accountId,
                            SessionId: activeSession.Id,
                            Details: new
                            {
                                snapshot.Name,
                                snapshot.LastUsedAt,
                                IdleMinutes = Math.Max(5, _options.AutoStopIdleMinutes)
                            }), cancellationToken);
                    }

                    continue;
                }

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
