namespace GhProxy.Api.Services;

public sealed class LocalProxyOptions
{
    public string ProbeUrl { get; set; } = "http://example.com/";
    public string? BindHostOverride { get; set; }
}
