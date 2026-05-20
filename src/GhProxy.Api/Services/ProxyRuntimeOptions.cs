namespace GhProxy.Api.Services;

public sealed class ProxyRuntimeOptions
{
    public int IdleShutdownMinutes { get; set; } = 30;
    public int ActivityProbeSeconds { get; set; } = 60;
    public string RemoteDirectory { get; set; } = "~/.gh-proxy";
    public string KnownHostsPath { get; set; } = "data/known_hosts";
    public string TunnelCommandName { get; set; } = "autossh";
}
