namespace GhProxy.Api.Domain;

public sealed class CodespaceStateSample
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public GitHubAccount? Account { get; set; }
    public string CodespaceName { get; set; } = string.Empty;
    public string State { get; set; } = "Unknown";
    public DateTimeOffset ObservedAt { get; set; }
    public string Source { get; set; } = "sync";
}
