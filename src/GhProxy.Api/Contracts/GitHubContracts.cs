using GhProxy.Api.Domain;

namespace GhProxy.Api.Contracts;

public sealed record GitHubAccountRequest(
    string DisplayName,
    string Username,
    string? PersonalAccessToken,
    string? Plan);

public sealed record GitHubAccountResponse(
    Guid Id,
    string DisplayName,
    string Username,
    string Plan,
    GitHubAccountValidationStatus ValidationStatus,
    GitHubAccountQuotaState QuotaState,
    string? ValidationMessage,
    string? LastError,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CodespaceSnapshotResponse(
    Guid Id,
    Guid AccountId,
    string Name,
    string State,
    string? RepositoryFullName,
    string? MachineDisplayName,
    string? Location,
    string? WebUrl,
    string? BillableOwner,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset LastSyncedAt);

public sealed record CreateCodespaceRequest(
    string RepositoryOwner,
    string RepositoryName,
    string? Ref,
    string? Geo,
    string? Machine,
    string? DisplayName,
    int? IdleTimeoutMinutes);

public sealed record GitHubUsageResponse(
    GitHubAccountQuotaState State,
    string Message,
    decimal? Quantity,
    string? UnitType,
    decimal? NetAmount,
    string BillingUrl);

public sealed record GitHubCodespaceExportResponse(
    string? Id,
    string? State,
    string? ExportUrl,
    string? HtmlUrl,
    DateTimeOffset? CompletedAt);

public sealed record GitHubLifecycleResultResponse(
    bool Succeeded,
    string Message,
    CodespaceSnapshotResponse? Codespace,
    GitHubCodespaceExportResponse? Export = null);
