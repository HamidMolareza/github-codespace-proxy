namespace GhProxy.Api.Services;

public sealed class ObservabilityOptions
{
    public string LogDirectory { get; set; } = "data/logs";
    public int RetentionDays { get; set; } = 14;
    public int MaxOutputChars { get; set; } = 4000;
    public bool EnableJsonlFile { get; set; } = true;
}
