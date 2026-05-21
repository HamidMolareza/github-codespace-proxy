using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class LocalProxyStartupRecoveryService(
    IServiceScopeFactory scopeFactory,
    LocalProxyRuntimeService runtime,
    IOperationalEventSink events,
    ILogger<LocalProxyStartupRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var staleSession = (await db.LocalProxySessions
                .AsNoTracking()
                .Where(x => x.Status == LocalProxySessionStatus.Error)
                .Where(x => x.LastError == DatabaseSchemaInitializer.RestartedActiveSessionMessage)
                .Where(x => x.AccountId != null && x.CodespaceName != null && x.CodespaceName != "")
                .ToListAsync(stoppingToken))
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefault();

            if (staleSession is null)
            {
                return;
            }

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.startup_recovery.requested",
                OperationalEventSeverity.Warning,
                "Recovering Codespace proxy after application restart.",
                NodeId: staleSession.AccountId,
                SessionId: staleSession.Id,
                Details: new { staleSession.ProfileId, staleSession.CodespaceName }),
                stoppingToken);

            var result = await runtime.StartCodespaceProxyAsync(
                staleSession.AccountId!.Value,
                staleSession.CodespaceName!,
                staleSession.ProfileId,
                stoppingToken);

            await events.WriteAsync(new OperationalEventWrite(
                result.Succeeded ? "local_proxy.startup_recovery.succeeded" : "local_proxy.startup_recovery.failed",
                result.Succeeded ? OperationalEventSeverity.Information : OperationalEventSeverity.Error,
                result.Message,
                NodeId: staleSession.AccountId,
                Details: new { staleSession.ProfileId, staleSession.CodespaceName }),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover Codespace proxy after application restart.");
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.startup_recovery.failed",
                OperationalEventSeverity.Error,
                "Failed to recover Codespace proxy after application restart.",
                StandardError: ex.Message),
                CancellationToken.None);
        }
    }
}
