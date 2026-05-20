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
    public async Task<GitHubAccount> ValidateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        try
        {
            var profile = await github.GetAuthenticatedUserAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), cancellationToken);
            account.Username = profile.Login;
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
        }

        foreach (var stale in existing.Where(x => !remoteNames.Contains(x.Name)).ToList())
        {
            db.CodespaceSnapshots.Remove(stale);
        }

        account.LastSyncedAt = now;
        account.LastError = null;
        account.UpdatedAt = now;
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
        var usage = await github.GetCodespacesUsageAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), account.Username, cancellationToken);
        account.QuotaState = usage.State;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return usage;
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

    public async Task ExportAsync(Guid accountId, string codespaceName, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(accountId, cancellationToken);
        await github.ExportCodespaceAsync(secrets.Unprotect(account.ProtectedPersonalAccessToken), codespaceName, cancellationToken);
        await audit.WriteAsync("github.codespaces.export", $"Requested export for Codespace {codespaceName}.", account.Id, cancellationToken);
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

        var remote = await action(secrets.Unprotect(account.ProtectedPersonalAccessToken), codespaceName, cancellationToken);
        var snapshot = await UpsertRemoteAsync(account, remote, cancellationToken);
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

        ApplyRemote(snapshot, remote, clock.UtcNow);
        account.LastSyncedAt = clock.UtcNow;
        account.LastError = null;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private async Task<GitHubAccount> GetAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.GitHubAccounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
        ?? throw new InvalidOperationException("GitHub account was not found.");

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
