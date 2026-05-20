namespace GhProxy.Api.Domain;

public enum GitHubAccountValidationStatus
{
    Unknown,
    Valid,
    Invalid,
    Error
}

public enum GitHubAccountQuotaState
{
    Unknown,
    Healthy,
    Warning,
    Limited,
    Unavailable
}

public sealed class GitHubAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProtectedPersonalAccessToken { get; set; } = string.Empty;
    public string Plan { get; set; } = "Unknown";
    public GitHubAccountValidationStatus ValidationStatus { get; set; } = GitHubAccountValidationStatus.Unknown;
    public GitHubAccountQuotaState QuotaState { get; set; } = GitHubAccountQuotaState.Unknown;
    public string? ValidationMessage { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CodespaceSnapshot> Codespaces { get; set; } = [];
}
