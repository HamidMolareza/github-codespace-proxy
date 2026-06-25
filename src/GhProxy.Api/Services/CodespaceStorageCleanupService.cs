using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class CodespaceStorageCleanupService(
    AppDbContext db,
    GitHubCodespaceService codespaces,
    IOperationalEventSink events,
    IOptions<GitHubOptions> githubOptions)
{
    private readonly GitHubOptions _githubOptions = githubOptions.Value;

    public async Task<CodespaceStorageCleanupResult> CleanupAsync(
        GitHubAccount account,
        GitHubUsageResponse? usage,
        IReadOnlyList<CodespaceSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (!_githubOptions.AutoDeleteStorageLimitedProxyCodespaces ||
            usage is null ||
            !IsStorageLimited(usage))
        {
            return new CodespaceStorageCleanupResult(snapshots, [], 0);
        }

        var runningNames = await db.LocalProxySessions
            .AsNoTracking()
            .Where(x => x.AccountId == account.Id)
            .Where(x => x.Status == LocalProxySessionStatus.Starting || x.Status == LocalProxySessionStatus.Running)
            .Where(x => x.CodespaceName != null && x.CodespaceName != "")
            .Select(x => x.CodespaceName!)
            .ToListAsync(cancellationToken);
        var protectedNames = runningNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var snapshot in snapshots.Where(IsEligible))
        {
            try
            {
                await codespaces.DeleteAsync(account.Id, snapshot.Name, cancellationToken);
                deleted.Add(snapshot.Name);
                warnings.Add($"Deleted storage-limited Codespace {account.Username}/{snapshot.Name}.");
                await events.WriteAsync(new OperationalEventWrite(
                    "github.codespaces.storage_limited_delete",
                    OperationalEventSeverity.Information,
                    "Deleted stopped app-managed Codespace because storage quota is limited.",
                    NodeId: account.Id,
                    Details: new { account.Username, snapshot.Name, snapshot.RepositoryFullName, usage.State }),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Could not delete storage-limited Codespace {account.Username}/{snapshot.Name}: {ex.Message}");
                await events.WriteAsync(new OperationalEventWrite(
                    "github.codespaces.storage_limited_delete.failed",
                    OperationalEventSeverity.Warning,
                    "Could not delete stopped app-managed Codespace after storage quota became limited.",
                    NodeId: account.Id,
                    StandardError: ex.Message,
                    Details: new { account.Username, snapshot.Name, snapshot.RepositoryFullName, usage.State }),
                    cancellationToken);
            }
        }

        return new CodespaceStorageCleanupResult(
            snapshots.Where(x => !deleted.Contains(x.Name)).ToList(),
            warnings,
            deleted.Count);

        bool IsEligible(CodespaceSnapshot snapshot) =>
            IsStoppedState(snapshot.State) &&
            CodespaceProxyRepositoryPolicy.IsProxyRepository(snapshot.RepositoryFullName, account.Username) &&
            !protectedNames.Contains(snapshot.Name);
    }

    private static bool IsStorageLimited(GitHubUsageResponse usage) =>
        usage.Quotas.Any(x =>
            x.Name.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
            x.PercentUsed is >= 100);

    private static bool IsStoppedState(string state) =>
        state.Equals("Shutdown", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Stopped", StringComparison.OrdinalIgnoreCase);
}

public sealed record CodespaceStorageCleanupResult(
    IReadOnlyList<CodespaceSnapshot> Snapshots,
    IReadOnlyList<string> Warnings,
    int DeletedCount);
