namespace GhProxy.Api.Services;

public sealed class LocalProxyIdleShutdownService(IServiceScopeFactory scopeFactory, ILogger<LocalProxyIdleShutdownService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runtime = scope.ServiceProvider.GetRequiredService<LocalProxyRuntimeService>();
                await runtime.RetryIfDueAsync(stoppingToken);
                await runtime.StopIfIdleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to evaluate local proxy idle shutdown.");
            }
        }
    }
}
