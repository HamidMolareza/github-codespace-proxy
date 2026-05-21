namespace GhProxy.Api.Domain;

public sealed class VpsNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUsername { get; set; } = "";
    public string SshKeyPath { get; set; } = "";
    public string? Region { get; set; }
    public string? Notes { get; set; }
    public int LocalPort { get; set; } = 8910;
    public int RemoteHttpPort { get; set; } = 3128;
    public int RemoteSocksPort { get; set; } = 1080;
    public string ProxyUsername { get; set; } = "";
    public string ProtectedProxyPassword { get; set; } = "";
    public VpsNodeStatus Status { get; set; } = VpsNodeStatus.Unknown;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProxySession> Sessions { get; set; } = [];
}

public enum VpsNodeStatus
{
    Unknown,
    Ready,
    Running,
    Stopped,
    Error
}
