using System.Net;
using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class GitHubCodespaceService(
    AppDbContext db,
    IGitHubApiClient github,
    ISecretProtector secrets,
    IClock clock,
    AuditService audit,
    IOperationalEventSink events)
{
    public async Task<GitHubAccount> CreateAccountAsync(GitHubAccountRequest request, CancellationToken cancellationToken)
    {
        var token = request.PersonalAccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Personal access token is required.");
        }

        var profile = await github.GetAuthenticatedUserAsync(token, cancellationToken);
        var username = NormalizeUsername(profile);
        if (await db.GitHubAccounts.AnyAsync(x => x.Username == username, cancellationToken))
        {
            throw new InvalidOperationException("A GitHub account with this username already exists.");
        }

        var now = clock.UtcNow;
        var account = new GitHubAccount
        {
            DisplayName = ResolveDisplayName(request.DisplayName, profile, username),
            Username = username,
            ProtectedPersonalAccessToken = secrets.Protect(token),
            Plan = ResolvePlan(request.Plan, profile.PlanName, "Unknown"),
            ValidationStatus = GitHubAccountValidationStatus.Valid,
            ValidationMessage = "Token validated.",
            LastValidatedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.GitHubAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("github.account.create", "Created GitHub account.", account.Id, cancellationToken);
        return account;
    }

    public async Task<GitHubAccount> UpdateAccountAsync(Guid accountId, GitHubAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        var token = request.PersonalAccessToken?.Trim();
        var now = clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(token))
        {
            var profile = await github.GetAuthenticatedUserAsync(token, cancellationToken);
            var username = NormalizeUsername(profile);
            if (await db.GitHubAccounts.AnyAsync(x => x.Id != accountId && x.Username == username, cancellationToken))
            {
                throw new InvalidOperationException("A GitHub account with this username already exists.");
            }

            account.Username = username;
            account.ProtectedPersonalAccessToken = secrets.Protect(token);
            account.DisplayName = ResolveDisplayName(request.DisplayName, profile, username);
            account.Plan = ResolvePlan(request.Plan, profile.PlanName, account.Plan);
            account.ValidationStatus = GitHubAccountValidationStatus.Valid;
            account.ValidationMessage = "Token validated.";
            account.LastError = null;
            account.LastValidatedAt = now;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.DisplayName))
            {
                account.DisplayName = request.DisplayName.Trim();
            }

            account.Plan = ResolvePlan(request.Plan, null, account.Plan);
        }

        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("github.account.update", "Updated GitHub account.", account.Id, cancellationToken);
        return account;
    }

    public async Task<IReadOnlyList<GitHubAccountStatusCheckResultResponse>> CheckAllStatusesAsync(CancellationToken cancellationToken)
    {
        var accountIds = await db.GitHubAccounts
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var results = new List<GitHubAccountStatusCheckResultResponse>(accountIds.Count);

        foreach (var accountId in accountIds)
        {
            try
            {
                var account = await ValidateAsync(accountId, cancellationToken);
                if (account.ValidationStatus != GitHubAccountValidationStatus.Valid)
                {
                    results.Add(new GitHubAccountStatusCheckResultResponse(
                        accountId,
                        false,
                        account.ValidationMessage ?? account.LastError ?? "Token validation failed."));
                    continue;
                }

                var snapshots = await SyncAsync(accountId, cancellationToken);
                var usage = await GetUsageAsync(accountId, cancellationToken);
                results.Add(new GitHubAccountStatusCheckResultResponse(
                    accountId,
                    true,
                    $"Synced {snapshots.Count} Codespace(s). Quota is {usage.State}."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var account = await db.GitHubAccounts.FindAsync([accountId], cancellationToken);
                if (account is not null)
                {
                    account.ValidationStatus = GitHubAccountValidationStatus.Error;
                    account.LastError = ex.Message;
                    account.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                await events.WriteAsync(new OperationalEventWrite(
                    "github.account.status_check.failed",
                    OperationalEventSeverity.Warning,
                    ex.Message,
                    NodeId: accountId), cancellationToken);
                results.Add(new GitHubAccountStatusCheckResultResponse(accountId, false, ex.Message));
            }
        }

        return results;
    }

    public async Task<GitHubAccount> ValidateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        try
        {
            var profile = await github.GetAuthenticatedUserAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), cancellationToken);
            var username = NormalizeUsername(profile);
            if (await db.GitHubAccounts.AnyAsync(x => x.Id != accountId && x.Username == username, cancellationToken))
            {
                account.ValidationStatus = GitHubAccountValidationStatus.Error;
                account.ValidationMessage = "Another GitHub account already uses this token username.";
                account.LastError = account.ValidationMessage;
                account.LastValidatedAt = clock.UtcNow;
                account.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return account;
            }

            account.Username = username;
            if (string.IsNullOrWhiteSpace(account.DisplayName))
            {
                account.DisplayName = ResolveDisplayName(null, profile, username);
            }

            if (string.IsNullOrWhiteSpace(account.Plan) || account.Plan.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                account.Plan = ResolvePlan(null, profile.PlanName, account.Plan);
            }

            account.ValidationStatus = GitHubAccountValidationStatus.Valid;
            account.ValidationMessage = "Token validated.";
            account.LastError = null;
            account.LastValidatedAt = clock.UtcNow;
            account.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("github.account.validate", "Validated GitHub account token.", account.Id, cancellationToken);
            return account;
        }
        catch (GitHubApiException ex)
        {
            account.ValidationStatus = ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? GitHubAccountValidationStatus.Invalid
                : GitHubAccountValidationStatus.Error;
            account.ValidationMessage = ex.Message;
            account.LastError = ex.Message;
            account.LastValidatedAt = clock.UtcNow;
            account.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "github.account.validate.failed",
                OperationalEventSeverity.Warning,
                ex.Message,
                NodeId: account.Id,
                Details: new { account.Username, statusCode = (int)ex.StatusCode }), cancellationToken);
            return account;
        }
    }

    public async Task<IReadOnlyList<CodespaceSnapshot>> SyncAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        var token = secrets.Unprotect(account.ProtectedPersonalAccessToken);
        var remoteCodespaces = await github.ListCodespacesAsync(token, cancellationToken);
        var now = clock.UtcNow;
        var remoteNames = remoteCodespaces.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await db.CodespaceSnapshots.Where(x => x.AccountId == accountId).ToListAsync(cancellationToken);

        foreach (var remote in remoteCodespaces)
        {
            var snapshot = existing.FirstOrDefault(x => string.Equals(x.Name, remote.Name, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null)
            {
                snapshot = new CodespaceSnapshot { AccountId = account.Id, Name = remote.Name };
                db.CodespaceSnapshots.Add(snapshot);
                existing.Add(snapshot);
            }

            ApplyRemote(snapshot, remote, now);
            AddStateSample(account.Id, remote.Name, remote.State, now, "sync");
        }

        foreach (var stale in existing.Where(x => !remoteNames.Contains(x.Name)).ToList())
        {
            db.CodespaceSnapshots.Remove(stale);
        }

        account.LastSyncedAt = now;
        account.LastError = null;
        account.UpdatedAt = now;
        DeleteOldStateSamples(now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("github.codespaces.sync", $"Synced {remoteCodespaces.Count} Codespaces.", account.Id, cancellationToken);

        return await db.CodespaceSnapshots
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderBy(x => x.RepositoryFullName)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<GitHubUsageResponse> GetUsageAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        var usage = ApplyPlanQuota(
            await github.GetCodespacesUsageAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), account.Username, cancellationToken),
            account.Plan);
        account.QuotaState = usage.State;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return usage;
    }

    public async Task<CodespaceSnapshot?> RefreshCodespaceAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken)
    {
        var snapshots = await SyncAsync(accountId, cancellationToken);
        return snapshots.FirstOrDefault(x => string.Equals(x.Name, codespaceName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CodespaceSnapshot> CreateAsync(Guid accountId, CreateCodespaceRequest request, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        if (account.QuotaState == GitHubAccountQuotaState.Limited)
        {
            throw new InvalidOperationException("This account is marked as limited. Creating new Codespaces is blocked.");
        }

        ValidateCreate(request);
        var remote = await github.CreateCodespaceAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), request, cancellationToken);
        var snapshot = await UpsertRemoteAsync(account, remote, cancellationToken);
        await audit.WriteAsync("github.codespaces.create", $"Created Codespace {snapshot.Name}.", account.Id, cancellationToken);
        return snapshot;
    }

    public Task<CodespaceSnapshot> StartAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken) =>
        RunLifecycleAsync(accountId, codespaceName, github.StartCodespaceAsync, "github.codespaces.start", "Started", cancellationToken);

    public Task<CodespaceSnapshot> StopAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken) =>
        RunLifecycleAsync(accountId, codespaceName, github.StopCodespaceAsync, "github.codespaces.stop", "Stopped", cancellationToken);

    public async Task DeleteAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        await github.DeleteCodespaceAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), codespaceName, cancellationToken);
        var snapshot = await db.CodespaceSnapshots.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Name == codespaceName, cancellationToken);
        if (snapshot is not null)
        {
            db.CodespaceSnapshots.Remove(snapshot);
        }

        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("github.codespaces.delete", $"Deleted Codespace {codespaceName}.", account.Id, cancellationToken);
    }

    public async Task<GitHubCodespaceExportResult> ExportAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        var token = secrets.Unprotect(account.ProtectedPersonalAccessToken);
        GitHubCodespaceExportRemote export;
        string? rejectionMessage = null;
        var acceptedNewExport = true;
        try
        {
            export = await github.ExportCodespaceAsync(token, codespaceName, cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            acceptedNewExport = false;
            rejectionMessage = ex.Message;
            var latest = await github.GetLatestCodespaceExportAsync(token, codespaceName, cancellationToken);
            if (latest is null)
            {
                throw;
            }

            await events.WriteAsync(new OperationalEventWrite(
                "github.codespaces.export.latest_loaded",
                OperationalEventSeverity.Warning,
                "GitHub did not accept a new Codespace export request, so the latest export was loaded.",
                NodeId: account.Id,
                StandardError: ex.Message,
                Details: new { account.Username, CodespaceName = codespaceName, latest.Id, latest.State }),
                cancellationToken);
            export = latest;
        }

        await audit.WriteAsync("github.codespaces.export", $"Requested export for Codespace {codespaceName}.", account.Id, cancellationToken);
        return new GitHubCodespaceExportResult(export, acceptedNewExport, rejectionMessage);
    }

    private async Task<CodespaceSnapshot> RunLifecycleAsync(
        Guid accountId,
        string codespaceName,
        Func<string, string, CancellationToken, Task<GitHubCodespaceRemote>> action,
        string eventType,
        string verb,
        CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        if (account.QuotaState == GitHubAccountQuotaState.Limited && verb == "Started")
        {
            throw new InvalidOperationException("This account is marked as limited. Starting Codespaces is blocked.");
        }

        GitHubCodespaceRemote remote;
        try
        {
            remote = await action(secrets.Unprotect(account.ProtectedPersonalAccessToken), codespaceName, cancellationToken);
        }
        catch (GitHubApiException ex) when (verb == "Started" && ex.StatusCode == HttpStatusCode.Conflict)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "github.codespaces.start.conflict",
                OperationalEventSeverity.Warning,
                "GitHub reported a Codespace start conflict; refreshing current Codespace state.",
                NodeId: account.Id,
                StandardError: ex.Body,
                Details: new { account.Username, CodespaceName = codespaceName, StatusCode = (int)ex.StatusCode }),
                cancellationToken);
            var refreshed = await SyncAsync(accountId, cancellationToken);
            var current = refreshed.FirstOrDefault(x => string.Equals(x.Name, codespaceName, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                await audit.WriteAsync(eventType, $"Codespace {current.Name} start is already in progress.", account.Id, cancellationToken);
                return current;
            }

            throw;
        }

        var snapshot = await UpsertRemoteAsync(account, remote, cancellationToken);
        var synced = await SyncAsync(accountId, cancellationToken);
        snapshot = synced.FirstOrDefault(x => string.Equals(x.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase)) ?? snapshot;
        await audit.WriteAsync(eventType, $"{verb} Codespace {snapshot.Name}.", account.Id, cancellationToken);
        return snapshot;
    }

    private async Task<CodespaceSnapshot> UpsertRemoteAsync(GitHubAccount account, GitHubCodespaceRemote remote, CancellationToken cancellationToken)
    {
        var snapshot = await db.CodespaceSnapshots.FirstOrDefaultAsync(x => x.AccountId == account.Id && x.Name == remote.Name, cancellationToken);
        if (snapshot is null)
        {
            snapshot = new CodespaceSnapshot { AccountId = account.Id, Name = remote.Name };
            db.CodespaceSnapshots.Add(snapshot);
        }

        var now = clock.UtcNow;
        ApplyRemote(snapshot, remote, now);
        AddStateSample(account.Id, remote.Name, remote.State, now, "lifecycle");
        DeleteOldStateSamples(now);
        account.LastSyncedAt = now;
        account.LastError = null;
        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private void AddStateSample(Guid accountId, string codespaceName, string state, DateTimeOffset observedAt, string source)
    {
        db.CodespaceStateSamples.Add(new CodespaceStateSample
        {
            AccountId = accountId,
            CodespaceName = codespaceName,
            State = state,
            ObservedAt = observedAt,
            Source = source
        });
    }

    private void DeleteOldStateSamples(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-90);
        db.CodespaceStateSamples.RemoveRange(db.CodespaceStateSamples.Where(x => x.ObservedAt < cutoff));
    }

    private async Task<GitHubAccount> GetAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.GitHubAccounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
        ?? throw new InvalidOperationException("GitHub account was not found.");

    private static GitHubUsageResponse ApplyPlanQuota(GitHubUsageResponse usage, string plan)
    {
        if (usage.State == GitHubAccountQuotaState.Unavailable)
        {
            return usage;
        }

        var planLimits = GetPlanLimits(plan);
        var quotas = EnsurePlanQuotaRows(usage.Quotas, planLimits)
            .Select(quota => ApplyLimit(quota, GetLimit(quota.Name, planLimits)))
            .ToList();
        var maxPercentUsed = quotas
            .Where(x => x.PercentUsed is not null)
            .Select(x => x.PercentUsed!.Value)
            .DefaultIfEmpty(-1)
            .Max();
        var state = maxPercentUsed switch
        {
            >= 100 => GitHubAccountQuotaState.Limited,
            >= 90 => GitHubAccountQuotaState.Warning,
            _ => usage.State
        };

        return usage with { State = state, Quotas = quotas };
    }

    private static IReadOnlyList<GitHubUsageQuotaSummaryResponse> EnsurePlanQuotaRows(
        IReadOnlyList<GitHubUsageQuotaSummaryResponse> quotas,
        (decimal? Compute, decimal? Storage) planLimits)
    {
        var next = quotas.ToList();
        if (planLimits.Compute is not null && !next.Any(x => x.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)))
        {
            next.Insert(0, new GitHubUsageQuotaSummaryResponse("Compute", 0, null, null, null, "included units"));
        }

        if (planLimits.Storage is not null && !next.Any(x => x.Name.Equals("Storage", StringComparison.OrdinalIgnoreCase)))
        {
            next.Add(new GitHubUsageQuotaSummaryResponse("Storage", 0, null, null, null, "GB-month"));
        }

        return next;
    }

    private static (decimal? Compute, decimal? Storage) GetPlanLimits(string plan) =>
        plan.Trim().ToLowerInvariant() switch
        {
            "free" => (120, 15),
            "pro" => (180, 20),
            _ => (null, null)
        };

    private static decimal? GetLimit(string quotaName, (decimal? Compute, decimal? Storage) limits) =>
        quotaName.Equals("Compute", StringComparison.OrdinalIgnoreCase)
            ? limits.Compute
            : quotaName.Equals("Storage", StringComparison.OrdinalIgnoreCase)
                ? limits.Storage
                : null;

    private static GitHubUsageQuotaSummaryResponse ApplyLimit(GitHubUsageQuotaSummaryResponse quota, decimal? limit)
    {
        if (limit is null)
        {
            return quota;
        }

        var remaining = Math.Max(0, limit.Value - quota.Used);
        decimal? percentUsed = limit.Value == 0 ? null : Math.Round(quota.Used / limit.Value * 100, 1);
        return quota with { Limit = limit, Remaining = remaining, PercentUsed = percentUsed };
    }

    private static void ApplyRemote(CodespaceSnapshot snapshot, GitHubCodespaceRemote remote, DateTimeOffset now)
    {
        snapshot.Name = remote.Name;
        snapshot.State = remote.State;
        snapshot.RepositoryFullName = remote.RepositoryFullName;
        snapshot.MachineDisplayName = remote.MachineDisplayName;
        snapshot.Location = remote.Location;
        snapshot.WebUrl = remote.WebUrl;
        snapshot.BillableOwner = remote.BillableOwner;
        snapshot.CreatedAt = remote.CreatedAt;
        snapshot.UpdatedAt = remote.UpdatedAt;
        snapshot.LastUsedAt = remote.LastUsedAt;
        snapshot.LastSyncedAt = now;
    }

    private static string NormalizeUsername(GitHubUserProfile profile)
    {
        var username = profile.Login.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("GitHub token did not return a username.");
        }

        return username;
    }

    private static string ResolveDisplayName(string? requestedDisplayName, GitHubUserProfile profile, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(requestedDisplayName))
        {
            return requestedDisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(profile.Name))
        {
            return profile.Name.Trim();
        }

        return fallback;
    }

    private static string ResolvePlan(string? requestedPlan, string? tokenPlan, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(requestedPlan))
        {
            var normalized = NormalizePlan(requestedPlan);
            if (normalized is not null && !normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized is null)
            {
                return requestedPlan.Trim();
            }
        }

        return NormalizePlan(tokenPlan) ?? (string.IsNullOrWhiteSpace(fallback) ? "Unknown" : fallback);
    }

    private static string? NormalizePlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return null;
        }

        return plan.Trim().ToLowerInvariant() switch
        {
            "free" => "Free",
            "pro" => "Pro",
            "unknown" => "Unknown",
            _ => null
        };
    }

    private static void ValidateCreate(CreateCodespaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
        {
            throw new InvalidOperationException("Repository owner is required.");
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            throw new InvalidOperationException("Repository name is required.");
        }

        if (request.IdleTimeoutMinutes is not null and (< 5 or > 240))
        {
            throw new InvalidOperationException("Idle timeout must be between 5 and 240 minutes.");
        }
    }
}

public sealed record GitHubCodespaceExportResult(
    GitHubCodespaceExportRemote Export,
    bool AcceptedNewExport,
    string? RejectionMessage);
