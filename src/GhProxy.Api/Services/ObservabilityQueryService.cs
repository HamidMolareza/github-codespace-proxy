using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class ObservabilityQueryService(
    AppDbContext db,
    IClock clock,
    IOptions<ObservabilityOptions> options)
{
    private readonly ObservabilityOptions _options = options.Value;

    public async Task<IReadOnlyList<OperationalEventResponse>> GetActivityAsync(
        Guid? nodeId,
        Guid? sessionId,
        string? severity,
        string? eventType,
        string? correlationId,
        string? search,
        int? limit,
        CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays)).ToUnixTimeMilliseconds();
        var query = db.OperationalEvents.AsNoTracking().Where(x => x.TimestampUtcMs >= cutoff);

        if (nodeId is not null)
        {
            query = query.Where(x => x.NodeId == nodeId);
        }

        if (sessionId is not null)
        {
            query = query.Where(x => x.SessionId == sessionId);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            var value = severity.Trim();
            query = query.Where(x => x.Severity == value);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var value = eventType.Trim();
            query = query.Where(x => x.EventType == value);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            var value = correlationId.Trim();
            query = query.Where(x => x.CorrelationId == value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            query = query.Where(x =>
                x.Message.Contains(value) ||
                x.EventType.Contains(value) ||
                (x.CommandDisplay != null && x.CommandDisplay.Contains(value)) ||
                (x.StandardErrorSnippet != null && x.StandardErrorSnippet.Contains(value)));
        }

        var take = Math.Clamp(limit ?? 100, 1, 500);
        var rows = await query
            .OrderByDescending(x => x.TimestampUtcMs)
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(ToResponse).ToList();
    }

    public async Task<ActivitySummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        var recent = db.OperationalEvents.AsNoTracking().Where(x => x.TimestampUtcMs >= since);
        var stats = await recent
            .GroupBy(_ => 1)
            .Select(group => new
            {
                RecentCount = group.Count(),
                ErrorCount = group.Count(x => x.Severity == OperationalEventSeverity.Error),
                WarningCount = group.Count(x => x.Severity == OperationalEventSeverity.Warning),
                CommandFailureCount = group.Count(x =>
                    x.CommandKind != null && (x.TimedOut || (x.ExitCode.HasValue && x.ExitCode.Value != 0))),
                AverageCommandDurationMs = group
                    .Where(x => x.DurationMs != null && x.CommandKind != null)
                    .Average(x => (double?)x.DurationMs)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var lastError = await recent
            .Where(x => x.Severity == OperationalEventSeverity.Error)
            .OrderByDescending(x => x.TimestampUtcMs)
            .FirstOrDefaultAsync(cancellationToken);

        return new ActivitySummaryResponse(
            stats?.RecentCount ?? 0,
            stats?.ErrorCount ?? 0,
            stats?.WarningCount ?? 0,
            stats?.CommandFailureCount ?? 0,
            stats?.AverageCommandDurationMs,
            lastError is null ? null : ToResponse(lastError));
    }

    private static OperationalEventResponse ToResponse(OperationalEvent evt) =>
        new(
            evt.Id,
            evt.Timestamp,
            evt.Severity,
            evt.EventType,
            evt.Message,
            evt.NodeId,
            evt.SessionId,
            evt.CorrelationId,
            evt.CommandKind,
            evt.CommandDisplay,
            evt.ExitCode,
            evt.DurationMs,
            evt.TimedOut,
            evt.StandardOutputSnippet,
            evt.StandardErrorSnippet,
            evt.DetailsJson);
}
