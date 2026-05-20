namespace GhProxy.Api.Domain;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? NodeId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string EventType { get; set; } = "";
    public string Message { get; set; } = "";
}
