namespace GhProxy.Api.Services;

public sealed class LocalProxyOptions
{
    public string ProbeUrl { get; set; } = "http://example.com/";
    public int ProbeTimeoutSeconds { get; set; } = 30;
    public string? BindHostOverride { get; set; }
    public bool RequireHttpProbe { get; set; } = true;
    public bool RequireSocksProbe { get; set; } = true;
    public string XrayExecutablePath { get; set; } = "xray";
    public string? XrayConfigDirectory { get; set; }
    public int CodespaceRemoteProxyPort { get; set; } = 8899;
    public int CodespaceRemoteDashboardPort { get; set; } = 8898;
    public string CodespaceRemoteProxyCommand { get; set; } = "proxy";
    public int CodespaceRemoteProxyStartupTimeoutSeconds { get; set; } = 120;
    public int CodespaceTunnelReadyTimeoutSeconds { get; set; } = 30;
    public string CodespaceTunnelMode { get; set; } = "native-ssh";
    public bool CodespaceReconnectOnTunnelExit { get; set; } = true;
    public int CodespaceStartTimeoutSeconds { get; set; } = 180;
    public int CodespaceRetryInitialSeconds { get; set; } = 15;
    public int CodespaceRetryMaxSeconds { get; set; } = 300;
    public bool CodespaceRequireSshReady { get; set; } = true;
    public bool CodespaceEnsureRemoteProxy { get; set; }
    public int DefaultIdleShutdownMinutes { get; set; } = 30;
    public int IdleWakeRequestThreshold { get; set; } = 5;
    public int IdleWakeWindowSeconds { get; set; } = 60;
    public string CodespaceRepositoryOwner { get; set; } = "wproxy97";
    public string CodespaceRepositoryName { get; set; } = "proxy2";
    public string CodespaceRepositoryRef { get; set; } = "main";
    public string CodespaceGeo { get; set; } = "UsEast";
    public string CodespaceMachine { get; set; } = "basicLinux32gb";
    public string CodespaceDisplayName { get; set; } = "gh-proxy";
}
