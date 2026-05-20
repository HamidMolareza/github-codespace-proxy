namespace GhProxy.Api.Domain;

public sealed class ProxySession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NodeId { get; set; }
    public VpsNode? Node { get; set; }
    public ProxySessionStatus Status { get; set; } = ProxySessionStatus.Starting;
    public int? TunnelProcessId { get; set; }
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? LastError { get; set; }
}

public enum ProxySessionStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}
