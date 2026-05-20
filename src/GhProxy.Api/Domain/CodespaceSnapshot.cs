namespace GhProxy.Api.Domain;

public sealed class CodespaceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public GitHubAccount? Account { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "Unknown";
    public string? RepositoryFullName { get; set; }
    public string? MachineDisplayName { get; set; }
    public string? Location { get; set; }
    public string? WebUrl { get; set; }
    public string? BillableOwner { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}
