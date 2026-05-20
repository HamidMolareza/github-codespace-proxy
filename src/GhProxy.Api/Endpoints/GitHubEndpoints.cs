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
            await db.GitHubAccounts
                .AsNoTracking()
                .OrderBy(x => x.DisplayName)
                .Select(x => ToResponse(x))
                .ToListAsync(ct));

        accounts.MapPost("/", async (GitHubAccountRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var validation = ValidateAccount(request, requireToken: true);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var username = request.Username.Trim();
            if (await db.GitHubAccounts.AnyAsync(x => x.Username == username, ct))
            {
                return Results.Conflict(new { error = "A GitHub account with this username already exists." });
            }

            var now = clock.UtcNow;
            var account = new GitHubAccount
            {
                DisplayName = request.DisplayName.Trim(),
                Username = username,
                ProtectedPersonalAccessToken = secrets.Protect(request.PersonalAccessToken!.Trim()),
                Plan = string.IsNullOrWhiteSpace(request.Plan) ? "Unknown" : request.Plan.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("github.account.create", "Created GitHub account.", account.Id, ct);
            return Results.Created($"/api/github/accounts/{account.Id}", ToResponse(account));
        });

        accounts.MapPut("/{id:guid}", async (Guid id, GitHubAccountRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var account = await db.GitHubAccounts.FindAsync([id], ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            var validation = ValidateAccount(request, requireToken: false);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var username = request.Username.Trim();
            if (await db.GitHubAccounts.AnyAsync(x => x.Id != id && x.Username == username, ct))
            {
                return Results.Conflict(new { error = "A GitHub account with this username already exists." });
            }

            account.DisplayName = request.DisplayName.Trim();
            account.Username = username;
            account.Plan = string.IsNullOrWhiteSpace(request.Plan) ? "Unknown" : request.Plan.Trim();
            account.UpdatedAt = clock.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.PersonalAccessToken))
            {
                account.ProtectedPersonalAccessToken = secrets.Protect(request.PersonalAccessToken.Trim());
                account.ValidationStatus = GitHubAccountValidationStatus.Unknown;
                account.ValidationMessage = null;
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("github.account.update", "Updated GitHub account.", account.Id, ct);
            return Results.Ok(ToResponse(account));
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

        accounts.MapPost("/{id:guid}/codespaces", async (Guid id, CreateCodespaceRequest request, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace create request submitted.", ToResponse(await service.CreateAsync(id, request, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/start", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace start request submitted.", ToResponse(await service.StartAsync(id, name, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/stop", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
            Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace stop request submitted.", ToResponse(await service.StopAsync(id, name, ct)))));

        accounts.MapPost("/{id:guid}/codespaces/{name}/export", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
        {
            await service.ExportAsync(id, name, ct);
            return Results.Ok(new GitHubLifecycleResultResponse(true, "Codespace export requested.", null));
        });

        accounts.MapDelete("/{id:guid}/codespaces/{name}", async (Guid id, string name, GitHubCodespaceService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, name, ct);
            return Results.NoContent();
        });

        return app;
    }

    private static GitHubAccountResponse ToResponse(GitHubAccount account) =>
        new(
            account.Id,
            account.DisplayName,
            account.Username,
            account.Plan,
            account.ValidationStatus,
            account.QuotaState,
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

    private static object? ValidateAccount(GitHubAccountRequest request, bool requireToken)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddRequired(errors, nameof(request.Username), request.Username);
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
