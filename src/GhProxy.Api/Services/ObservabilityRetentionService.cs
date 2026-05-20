using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class ObservabilityRetentionService(IOptions<ObservabilityOptions> options, ILogger<ObservabilityRetentionService> logger)
    : BackgroundService
{
    private readonly ObservabilityOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Cleanup();
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private void Cleanup()
    {
        if (!_options.EnableJsonlFile || _options.RetentionDays <= 0 || !Directory.Exists(_options.LogDirectory))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
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
