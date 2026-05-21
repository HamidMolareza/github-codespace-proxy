using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
            IEnumerable<OperationalEvent> query = await db.OperationalEvents.AsNoTracking().ToListAsync(ct);

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
            var events = query
                .OrderByDescending(x => x.Timestamp)
                .Take(take)
                .Select(x => ToResponse(x))
                .ToList();

            return Results.Ok(events);
        });

        group.MapGet("/activity/summary", async (AppDbContext db, CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var recent = (await db.OperationalEvents.AsNoTracking().ToListAsync(ct))
                .Where(x => x.Timestamp >= since)
                .ToList();

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
                recent.Count(x => x.CommandKind != null && (x.TimedOut || (x.ExitCode.HasValue && x.ExitCode.Value != 0))),
                commandDurations.Count == 0 ? null : commandDurations.Average(),
                lastError is null ? null : ToResponse(lastError)));
        });

        group.MapDelete("/activity", async (AppDbContext db, IOptions<ObservabilityOptions> options, CancellationToken ct) =>
        {
            var deleted = await db.OperationalEvents.ExecuteDeleteAsync(ct);
            var deletedFiles = DeleteOperationalJsonlFiles(options.Value.LogDirectory);
            return Results.Ok(new ActivityClearResponse(deleted, deletedFiles));
        });

        group.MapGet("/diagnostics/runtime", async (AppDbContext db, IOptions<GitHubOptions> githubOptions, IOptions<LocalProxyOptions> localProxyOptions, IHostEnvironment environment, IRuntimeToolChecker toolChecker, CancellationToken ct) =>
        {
            var databaseAvailable = await db.Database.CanConnectAsync(ct);
            var tools = new List<ToolDiagnosticResponse>
            {
                new("GitHub API", Uri.TryCreate(githubOptions.Value.ApiBaseUrl, UriKind.Absolute, out _), githubOptions.Value.ApiBaseUrl),
                new("Data Protection", Directory.Exists(Path.Combine(environment.ContentRootPath, "data", "keys")), "Keys are persisted under the app data directory."),
                new("GitHub direct networking", true, "GitHub REST and Codespaces commands bypass proxy environment variables.")
            };
            tools.AddRange(toolChecker
                .GetRuntimeDiagnostics(localProxyOptions.Value.XrayExecutablePath)
                .Select(x => new ToolDiagnosticResponse(x.Name, x.Available, x.Message)));

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

    private static int DeleteOperationalJsonlFiles(string logDirectory)
    {
        var directory = Path.GetFullPath(logDirectory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "operational-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

}
