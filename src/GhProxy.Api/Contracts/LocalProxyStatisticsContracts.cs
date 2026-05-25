namespace GhProxy.Api.Contracts;

public sealed record LocalProxyStatisticsResponse(
    string Period,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    string TimeZone,
    LocalProxyStatisticsTotalsResponse Totals,
    IReadOnlyList<LocalProxyStatisticsBucketResponse> HourlyBuckets,
    IReadOnlyList<LocalProxyStatisticsBucketResponse> DailyBuckets,
    IReadOnlyList<LocalProxyStatisticsSessionResponse> Sessions,
    IReadOnlyList<CodespaceStateSampleResponse> GitHubSamples,
    IReadOnlyList<LocalProxyStatisticsMismatchResponse> Mismatches);

public sealed record LocalProxyStatisticsTotalsResponse(
    long ActiveSeconds,
    long OffSeconds,
    long ErrorSeconds,
    double ActivePercent,
    double ErrorPercent,
    int SessionCount,
    double AverageActiveSecondsPerDay);

public sealed record LocalProxyStatisticsBucketResponse(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Label,
    long ActiveSeconds,
    long OffSeconds,
    long ErrorSeconds,
    double ActivePercent,
    double ErrorPercent,
    int SessionCount);

public sealed record LocalProxyStatisticsSessionResponse(
    Guid SessionId,
    Guid? AccountId,
    string? AccountUsername,
    string? CodespaceName,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long ActiveSeconds,
    string Status,
    string? LastError);

public sealed record CodespaceStateSampleResponse(
    Guid AccountId,
    string? AccountUsername,
    string CodespaceName,
    string State,
    DateTimeOffset ObservedAt,
    string Source,
    bool IsActive);

public sealed record LocalProxyStatisticsMismatchResponse(
    DateTimeOffset ObservedAt,
    Guid AccountId,
    string? AccountUsername,
    string CodespaceName,
    string GitHubState,
    string Message);
