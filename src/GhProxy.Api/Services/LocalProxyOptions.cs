namespace GhProxy.Api.Services;

public sealed class LocalProxyOptions
{
    public string ProbeUrl { get; set; } = "http://example.com/";
    public string? BindHostOverride { get; set; }
    public string XrayExecutablePath { get; set; } = "xray";
    public string? XrayConfigDirectory { get; set; }
    public string? CodespaceSshConfigDirectory { get; set; }
    public int CodespaceRemoteProxyPort { get; set; } = 8899;
    public int CodespaceTunnelReadyTimeoutSeconds { get; set; } = 30;
    public int CodespaceStartTimeoutSeconds { get; set; } = 180;
    public int CodespaceSshConfigTimeoutSeconds { get; set; } = 120;
}
