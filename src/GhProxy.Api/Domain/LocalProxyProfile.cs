namespace GhProxy.Api.Domain;

public sealed class LocalProxyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string BindHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; } = 8910;
    public int SocksPort { get; set; } = 8910;
    public string? ProxyUsername { get; set; }
    public string? ProtectedProxyPassword { get; set; }
    public int IdleShutdownMinutes { get; set; } = 30;
    public string? Notes { get; set; }
    public LocalProxyProfileStatus Status { get; set; } = LocalProxyProfileStatus.Stopped;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<LocalProxySession> Sessions { get; set; } = [];
}

public enum LocalProxyProfileStatus
{
    Stopped,
    Starting,
    Running,
    Error
}
