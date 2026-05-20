namespace GhProxy.Api.Services;

public sealed class GitHubOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    public string ApiVersion { get; set; } = "2026-03-10";
    public int SyncIntervalSeconds { get; set; } = 300;
    public int AutoStopIdleMinutes { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 30;
}
