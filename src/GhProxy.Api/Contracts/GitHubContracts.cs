using GhProxy.Api.Domain;

namespace GhProxy.Api.Contracts;

public sealed record GitHubAccountRequest(
    string? DisplayName,
    string? Username,
    string? PersonalAccessToken,
    string? Plan);

public sealed record GitHubAccountResponse(
    Guid Id,
    string DisplayName,
    string Username,
    string Plan,
    GitHubAccountValidationStatus ValidationStatus,
    GitHubAccountQuotaState QuotaState,
    int ActiveCodespaceCount,
    int TotalCodespaceCount,
    string? ValidationMessage,
    string? LastError,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GitHubAccountStatusCheckResponse(
    IReadOnlyList<GitHubAccountResponse> Accounts,
    IReadOnlyList<GitHubAccountStatusCheckResultResponse> Results);

public sealed record GitHubAccountStatusCheckResultResponse(
    Guid AccountId,
    bool Succeeded,
    string Message);

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
    string BillingUrl,
    IReadOnlyList<GitHubUsageQuotaSummaryResponse> Quotas,
    int? BillingPeriodYear = null,
    int? BillingPeriodMonth = null,
    DateTimeOffset? ResetAt = null);

public sealed record GitHubUsageQuotaSummaryResponse(
    string Name,
    decimal Used,
    decimal? Limit,
    decimal? Remaining,
    decimal? PercentUsed,
    string Unit);

public sealed record GitHubUsageForecastResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? ResetAt,
    int DaysUntilReset,
    decimal TotalComputeUsed,
    decimal TotalComputeLimit,
    decimal TotalComputeRemaining,
    decimal Average7DayComputeUsage,
    decimal Average14DayComputeUsage,
    decimal Average30DayComputeUsage,
    decimal EstimatedDailyComputeUsage,
    decimal? EstimatedQuotaDays,
    int? EstimatedUsableDays,
    string Status,
    string Message,
    int IncludedAccountCount,
    int UnavailableAccountCount,
    decimal DefaultMachineCoreCount,
    IReadOnlyList<string> Warnings);

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
