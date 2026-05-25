namespace GhProxy.Api.Domain;

public sealed class LocalProxyGatewayRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ObservedAt { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string? TargetHost { get; set; }
    public int? TargetPort { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? AccountId { get; set; }
    public string? CodespaceName { get; set; }
    public string? ErrorMessage { get; set; }
    public long? DurationMs { get; set; }
}
