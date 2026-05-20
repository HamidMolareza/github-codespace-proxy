namespace GhProxy.Api.Domain;

public sealed class OperationalEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; }
    public string Severity { get; set; } = "Information";
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Guid? NodeId { get; set; }
    public Guid? SessionId { get; set; }
    public string? CorrelationId { get; set; }
    public string? CommandKind { get; set; }
    public string? CommandDisplay { get; set; }
    public int? ExitCode { get; set; }
    public long? DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public string? StandardOutputSnippet { get; set; }
    public string? StandardErrorSnippet { get; set; }
    public string? DetailsJson { get; set; }
}
