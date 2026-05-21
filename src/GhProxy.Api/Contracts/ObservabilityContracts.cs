namespace GhProxy.Api.Contracts;

public sealed record OperationalEventResponse(
    Guid Id,
    DateTimeOffset Timestamp,
    string Severity,
    string EventType,
    string Message,
    Guid? NodeId,
    Guid? SessionId,
    string? CorrelationId,
    string? CommandKind,
    string? CommandDisplay,
    int? ExitCode,
    long? DurationMs,
    bool TimedOut,
    string? StandardOutputSnippet,
    string? StandardErrorSnippet,
    string? DetailsJson);

public sealed record ActivitySummaryResponse(
    int RecentCount,
    int ErrorCount,
    int WarningCount,
    int CommandFailureCount,
    double? AverageCommandDurationMs,
    OperationalEventResponse? LastError);

public sealed record ActivityClearResponse(int DeletedCount, int DeletedFileCount);

public sealed record RuntimeDiagnosticsResponse(
    bool DatabaseAvailable,
    IReadOnlyList<ToolDiagnosticResponse> Tools);

public sealed record ToolDiagnosticResponse(string Name, bool Available, string Message);
