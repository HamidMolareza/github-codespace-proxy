namespace GhProxy.Api.Domain;

public sealed class LocalProxySession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public LocalProxyProfile? Profile { get; set; }
    public LocalProxySessionStatus Status { get; set; } = LocalProxySessionStatus.Starting;
    public string BindHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; } = 8901;
    public int SocksPort { get; set; } = 8901;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? LastError { get; set; }
    public long TotalRequests { get; set; }
    public long TotalConnectTunnels { get; set; }
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
}

public enum LocalProxySessionStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}
