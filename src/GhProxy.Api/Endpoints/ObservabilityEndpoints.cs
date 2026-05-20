using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class ObservabilityEndpoints
{
    public static IEndpointRouteBuilder MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/activity", async (
            Guid? nodeId,
            Guid? sessionId,
            string? severity,
            string? eventType,
            string? correlationId,
            string? search,
            int? limit,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.OperationalEvents.AsNoTracking();

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
                query = query.Where(x => x.Severity == severity.Trim());
            }

            if (!string.IsNullOrWhiteSpace(eventType))
            {
                query = query.Where(x => x.EventType == eventType.Trim());
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(x => x.CorrelationId == correlationId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var text = search.Trim();
                query = query.Where(x =>
                    x.Message.Contains(text) ||
                    x.EventType.Contains(text) ||
                    (x.CommandDisplay != null && x.CommandDisplay.Contains(text)) ||
                    (x.StandardErrorSnippet != null && x.StandardErrorSnippet.Contains(text)));
            }

            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await query
                .OrderByDescending(x => x.Timestamp)
                .Take(take)
                .Select(x => ToResponse(x))
                .ToListAsync(ct);

            return Results.Ok(events);
        });

        group.MapGet("/activity/summary", async (AppDbContext db, CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var recent = await db.OperationalEvents
                .AsNoTracking()
                .Where(x => x.Timestamp >= since)
                .ToListAsync(ct);

            var lastError = recent
                .Where(x => x.Severity == OperationalEventSeverity.Error)
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            var commandDurations = recent
                .Where(x => x.DurationMs is not null && x.CommandKind != null)
                .Select(x => x.DurationMs!.Value)
                .ToList();

            return Results.Ok(new ActivitySummaryResponse(
                recent.Count,
                recent.Count(x => x.Severity == OperationalEventSeverity.Error),
                recent.Count(x => x.Severity == OperationalEventSeverity.Warning),
                recent.Count(x => x.CommandKind != null && (x.ExitCode != 0 || x.TimedOut)),
                commandDurations.Count == 0 ? null : commandDurations.Average(),
                lastError is null ? null : ToResponse(lastError)));
        });

        group.MapGet("/diagnostics/runtime", async (AppDbContext db, ICommandRunner runner, CancellationToken ct) =>
        {
            var databaseAvailable = await db.Database.CanConnectAsync(ct);
            var tools = new List<ToolDiagnosticResponse>();
            foreach (var tool in new[] { "ssh", "scp", "autossh", "ss" })
            {
                var result = await runner.RunAsync(new CommandSpec("sh", ["-lc", $"command -v {tool}"], TimeSpan.FromSeconds(3), $"diagnostic.{tool}"), ct);
                tools.Add(new ToolDiagnosticResponse(
                    tool,
                    result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput),
                    result.Succeeded ? result.StandardOutput.Trim() : result.StandardError.Trim()));
            }

            return Results.Ok(new RuntimeDiagnosticsResponse(databaseAvailable, tools));
        });

        return app;
    }

    private static OperationalEventResponse ToResponse(OperationalEvent evt)
    {
        return new OperationalEventResponse(
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
}
