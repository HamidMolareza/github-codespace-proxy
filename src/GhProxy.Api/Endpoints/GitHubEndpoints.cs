using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class GitHubEndpoints
{
    public static IEndpointRouteBuilder MapGitHubEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/github/accounts");

        accounts.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            (await db.GitHubAccounts
                .AsNoTracking()
                .Include(x => x.Codespaces)
                .OrderBy(x => x.DisplayName)
                .ToListAsync(ct))
            .Select(ToResponse)
            .ToList());

        accounts.MapPost("/", async (GitHubAccountRequest request, GitHubCodespaceService service, CancellationToken ct) =>
        {
            var validation = ValidateAccount(request, requireToken: true);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            try
            {
                var account = await service.CreateAccountAsync(request, ct);
                return Results.Created($"/api/github/accounts/{account.Id}", ToResponse(account));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (GitHubApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        accounts.MapPut("/{id:guid}", async (Guid id, GitHubAccountRequest request, GitHubCodespaceService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(ToResponse(await service.UpdateAccountAsync(id, request, ct)));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (GitHubApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        accounts.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var account = await db.GitHubAccounts.FindAsync([id], ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            db.GitHubAccounts.Remove(account);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("github.account.delete", "Deleted GitHub account.", id, ct);
            return Results.NoContent();
        });

        accounts.MapPost("/{id:guid}/validate", async (Guid id, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(ToResponse(await service.ValidateAsync(id, ct))));

        accounts.MapPost("/{id:guid}/sync", async (Guid id, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok((await service.SyncAsync(id, ct)).Select(ToResponse)));

        accounts.MapPost("/status-check", async (GitHubCodespaceService service, AppDbContext db, CancellationToken ct) =>
        {
            var results = await service.CheckAllStatusesAsync(ct);
            var nextAccounts = (await db.GitHubAccounts
                    .AsNoTracking()
                    .Include(x => x.Codespaces)
                    .OrderBy(x => x.DisplayName)
                    .ToListAsync(ct))
                .Select(ToResponse)
                .ToList();
            return Results.Ok(new GitHubAccountStatusCheckResponse(nextAccounts, results));
        });

        accounts.MapGet("/usage-forecast", async (GitHubUsageForecastService forecast, CancellationToken ct) =>
            Results.Ok(await forecast.GetAsync(ct)));

        accounts.MapGet("/{id:guid}/usage", async (Guid id, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(await service.GetUsageAsync(id, ct)));

        accounts.MapGet("/{id:guid}/codespaces", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var exists = await db.GitHubAccounts.AnyAsync(x => x.Id == id, ct);
            if (!exists)
            {
                return Results.NotFound();
            }

            var snapshots = await db.CodespaceSnapshots
                .AsNoTracking()
                .Where(x => x.AccountId == id)
                .OrderBy(x => x.RepositoryFullName)
                .ThenBy(x => x.Name)
                .Select(x => ToResponse(x))
                .ToListAsync(ct);
            return Results.Ok(snapshots);
        });

        accounts.MapGet("/{id:guid}/codespaces/{name}", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
        {
            var snapshot = await service.RefreshCodespaceAsync(id, name, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(ToResponse(snapshot));
        });

        accounts.MapPost("/{id:guid}/codespaces", async (Guid id, CreateCodespaceRequest request, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace create request submitted.", ToResponse(await service.CreateAsync(id, request, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/start", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace start request submitted.", ToResponse(await service.StartAsync(id, name, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/proxy/start", async (Guid id, string name, CodespaceProxyStartRequest request, LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.StartCodespaceProxyAsync(id, name, request.ProfileId, ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, LocalProxyEndpoints.ToResponse(result.Session)));
        });

        accounts.MapPost("/{id:guid}/codespaces/{name}/stop", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace stop request submitted.", ToResponse(await service.StopAsync(id, name, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/export", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
        {
            var result = await service.ExportAsync(id, name, ct);
            var export = result.Export;
            var state = string.IsNullOrWhiteSpace(export.State) ? "pending" : export.State;
            var exportLabel = string.IsNullOrWhiteSpace(export.Id) ? "export" : $"export {export.Id}";
            var stateFailed = state.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                              state.Equals("error", StringComparison.OrdinalIgnoreCase);
            var succeeded = !stateFailed;
            var prefix = result.AcceptedNewExport
                ? "Codespace export requested."
                : "GitHub rejected a new Codespace export request; loaded the latest export instead.";
            var message = $"{prefix} {ToSentenceCase(exportLabel)} is {state}.";
            if (!string.IsNullOrWhiteSpace(result.RejectionMessage))
            {
                message = $"{message} GitHub response: {result.RejectionMessage}";
            }

            return Results.Ok(new GitHubLifecycleResultResponse(succeeded, message, null, ToResponse(export)));
        });

        accounts.MapDelete("/{id:guid}/codespaces/{name}", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, name, ct);
            return Results.NoContent();
        });

        return app;
    }

    internal static GitHubAccountResponse ToResponse(GitHubAccount account) =>
        new(
            account.Id,
            account.DisplayName,
            account.Username,
            account.Plan,
            account.ValidationStatus,
            account.QuotaState,
            account.Codespaces.Count(IsActiveCodespace),
            account.Codespaces.Count,
            account.ValidationMessage,
            account.LastError,
            account.LastValidatedAt,
            account.LastSyncedAt,
            account.CreatedAt,
            account.UpdatedAt);

    private static CodespaceSnapshotResponse ToResponse(CodespaceSnapshot codespace) =>
        new(
            codespace.Id,
            codespace.AccountId,
            codespace.Name,
            codespace.State,
            codespace.RepositoryFullName,
            codespace.MachineDisplayName,
            codespace.Location,
            codespace.WebUrl,
            codespace.BillableOwner,
            codespace.CreatedAt,
            codespace.UpdatedAt,
            codespace.LastUsedAt,
            codespace.LastSyncedAt);

    private static GitHubCodespaceExportResponse ToResponse(GitHubCodespaceExportRemote export) =>
        new(
            export.Id,
            export.State,
            export.ExportUrl,
            export.HtmlUrl,
            export.CompletedAt);

    internal static bool IsActiveCodespace(CodespaceSnapshot codespace) =>
        codespace.State.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        codespace.State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
        codespace.State.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        codespace.State.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        codespace.State.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

    private static string ToSentenceCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : string.Concat(char.ToUpperInvariant(value[0]).ToString(), value[1..]);

    private static object? ValidateAccount(GitHubAccountRequest request, bool requireToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (requireToken)
        {
            AddRequired(errors, nameof(request.PersonalAccessToken), request.PersonalAccessToken);
        }

        return errors.Count == 0 ? null : new { errors };
    }

    private static void AddRequired(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Value is required."];
        }
    }
}
