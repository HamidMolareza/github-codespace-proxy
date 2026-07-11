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
            ObservabilityQueryService queries,
            CancellationToken ct) =>
        {
            return Results.Ok(await queries.GetActivityAsync(
                nodeId, sessionId, severity, eventType, correlationId, search, limit, ct));
        });

        group.MapGet("/activity/summary", async (ObservabilityQueryService queries, CancellationToken ct) =>
            Results.Ok(await queries.GetSummaryAsync(ct)));

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
