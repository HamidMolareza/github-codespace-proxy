namespace GhProxy.Api.Services;

public interface ICorrelationContext
{
    string? CorrelationId { get; set; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
