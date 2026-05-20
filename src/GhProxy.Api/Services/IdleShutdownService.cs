using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class IdleShutdownService(IServiceScopeFactory scopeFactory, IOptions<ProxyRuntimeOptions> options, ILogger<IdleShutdownService> logger)
    : BackgroundService
{
    private readonly ProxyRuntimeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.ActivityProbeSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProbeAndStopIdleSessionsAsync(stoppingToken);
        }
    }

    private async Task ProbeAndStopIdleSessionsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<ICommandRunner>();
        var tunnelService = scope.ServiceProvider.GetRequiredService<TunnelService>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var sessions = await db.ProxySessions
            .Include(x => x.Node)
            .Where(x => x.Status == ProxySessionStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            if (await HasActiveLocalConnectionAsync(runner, session.LocalPort, cancellationToken))
            {
                await tunnelService.MarkActivityAsync(session, cancellationToken);
                continue;
            }

            var idleFor = clock.UtcNow - session.LastActivityAt;
            if (idleFor >= TimeSpan.FromMinutes(Math.Max(1, _options.IdleShutdownMinutes)))
            {
                logger.LogInformation("Stopping idle proxy session {SessionId} after {IdleMinutes:n1} minutes.", session.Id, idleFor.TotalMinutes);
                await tunnelService.StopSessionAsync(session, "Stopped after idle timeout.", cancellationToken);
            }
        }
    }

    private static async Task<bool> HasActiveLocalConnectionAsync(ICommandRunner runner, int localPort, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new CommandSpec("ss", ["-Htn", $"sport = :{localPort}"], TimeSpan.FromSeconds(5)), cancellationToken);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }
}
