using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class CodespaceProxyAutomationService(
    AppDbContext db,
    GitHubCodespaceService codespaces,
    CodespaceStorageCleanupService storageCleanup,
    IGitHubApiClient github,
    ISecretProtector secrets,
    IOperationalEventSink events,
    IOptions<LocalProxyOptions> options)
{
    private readonly LocalProxyOptions _options = options.Value;

    public async Task<CodespaceProxySelectionResult> SelectAsync(CancellationToken cancellationToken)
    {
        var accounts = await db.GitHubAccounts
            .AsNoTracking()
            .Where(x => x.ValidationStatus != GitHubAccountValidationStatus.Invalid)
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return CodespaceProxySelectionResult.Fail("No usable GitHub accounts are configured.");
        }

        var candidates = new List<AccountCandidate>();
        var warnings = new List<string>();
        foreach (var account in accounts.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            GitHubUsageResponse? usage = null;
            try
            {
                usage = await codespaces.GetUsageAsync(account.Id, cancellationToken);
                if (usage.State == GitHubAccountQuotaState.Limited)
                {
                    await events.WriteAsync(new OperationalEventWrite(
                        "codespace_proxy.account.skipped_limited",
                        OperationalEventSeverity.Warning,
                        "GitHub account Codespaces quota is limited; new Codespaces and stopped starts are blocked.",
                        NodeId: account.Id,
                        Details: new { account.Username, usage.Message }),
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is GitHubApiException or InvalidOperationException)
            {
                warnings.Add($"Could not read usage for {account.Username}: {ex.Message}");
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.account.usage_failed",
                    OperationalEventSeverity.Warning,
                    "Could not read Codespaces usage for GitHub account.",
                    NodeId: account.Id,
                    StandardError: ex.Message,
                    Details: new { account.Username }),
                    cancellationToken);
            }

            IReadOnlyList<CodespaceSnapshot> snapshots;
            try
            {
                snapshots = await codespaces.SyncAsync(account.Id, cancellationToken);
                if (usage is not null)
                {
                    var cleanup = await storageCleanup.CleanupAsync(account, usage, snapshots, cancellationToken);
                    snapshots = cleanup.Snapshots;
                    warnings.AddRange(cleanup.Warnings);
                }
            }
            catch (Exception ex) when (ex is GitHubApiException or InvalidOperationException)
            {
                warnings.Add($"Could not sync {account.Username}: {ex.Message}");
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.account.sync_failed",
                    OperationalEventSeverity.Warning,
                    "Could not sync Codespaces for GitHub account during proxy selection.",
                    NodeId: account.Id,
                    StandardError: ex.Message,
                    Details: new { account.Username }),
                    cancellationToken);
                snapshots = [];
            }

            candidates.Add(new AccountCandidate(account, usage, snapshots));
        }

        var selectedExisting = candidates
            .SelectMany(ToCodespaceCandidates)
            .OrderBy(x => CodespaceRank(x.Codespace.State))
            .ThenByDescending(x => x.Codespace.LastUsedAt ?? x.Codespace.UpdatedAt ?? x.Codespace.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => UsageRank(x.Account.Usage?.State))
            .ThenBy(x => x.Account.Usage?.Quantity ?? decimal.MaxValue)
            .ThenBy(x => x.Account.Account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        AccountCandidate selectedAccount;
        CodespaceSnapshot selectedCodespace;
        var createdNewCodespace = false;
        if (selectedExisting is not null)
        {
            selectedAccount = selectedExisting.Account;
            selectedCodespace = selectedExisting.Codespace;
            if (IsStoppedState(selectedCodespace.State) &&
                selectedAccount.Usage?.State == GitHubAccountQuotaState.Limited)
            {
                return CodespaceProxySelectionResult.Fail($"Existing Codespace {selectedAccount.Account.Username}/{selectedCodespace.Name} is stopped, but the account quota is limited so it cannot be started.");
            }
        }
        else
        {
            var createAccount = candidates
                .Where(x => x.Usage?.State != GitHubAccountQuotaState.Limited)
                .OrderBy(x => UsageRank(x.Usage?.State))
                .ThenBy(x => x.Usage?.Quantity ?? decimal.MaxValue)
                .ThenBy(x => x.Account.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (createAccount is null)
            {
                return CodespaceProxySelectionResult.Fail("No GitHub account with usable Codespaces quota is available.");
            }

            selectedAccount = createAccount;
            var token = secrets.Unprotect(selectedAccount.Account.ProtectedPersonalAccessToken);
            await EnsureForkAsync(selectedAccount.Account, token, cancellationToken);
            var snapshots = await codespaces.SyncAsync(selectedAccount.Account.Id, cancellationToken);
            var repositoryFullName = $"{selectedAccount.Account.Username}/{_options.CodespaceRepositoryName}";
            var existingAfterFork = PickCodespace(snapshots, repositoryFullName);
            if (existingAfterFork is not null)
            {
                selectedCodespace = existingAfterFork;
            }
            else
            {
                createdNewCodespace = true;
                selectedCodespace = await codespaces.CreateAsync(
                    selectedAccount.Account.Id,
                    new CreateCodespaceRequest(
                        selectedAccount.Account.Username,
                        _options.CodespaceRepositoryName,
                        _options.CodespaceRepositoryRef,
                        _options.CodespaceGeo,
                        _options.CodespaceMachine,
                        _options.CodespaceDisplayName,
                        30),
                    cancellationToken);
            }
        }

        var selectedRepositoryFullName = $"{selectedAccount.Account.Username}/{_options.CodespaceRepositoryName}";
        warnings.AddRange(await StopExtraCodespacesAsync(accounts, selectedAccount.Account.Id, selectedCodespace.Name, cancellationToken));
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.account.selected",
            OperationalEventSeverity.Information,
            createdNewCodespace
                ? "Created and selected GitHub account and Codespace for automatic proxy startup."
                : "Selected existing GitHub account and Codespace for automatic proxy startup.",
            NodeId: selectedAccount.Account.Id,
            Details: new
            {
                selectedAccount.Account.Username,
                CodespaceName = selectedCodespace.Name,
                selectedCodespace.State,
                selectedAccount.Usage?.Quantity,
                selectedAccount.Usage?.UnitType,
                ReusedExistingCodespace = !createdNewCodespace,
                WarningCount = warnings.Count
            }),
            cancellationToken);

        return CodespaceProxySelectionResult.Ok(new CodespaceProxySelection(
            selectedAccount.Account.Id,
            selectedAccount.Account.Username,
            selectedCodespace.Name,
            selectedRepositoryFullName,
            warnings.Count == 0 ? null : string.Join(" ", warnings)));

        IEnumerable<CodespaceCandidate> ToCodespaceCandidates(AccountCandidate account) =>
            account.Snapshots
                .Where(x => IsReusableState(x.State))
                .Where(x => string.Equals(x.RepositoryFullName, $"{account.Account.Username}/{_options.CodespaceRepositoryName}", StringComparison.OrdinalIgnoreCase))
                .Where(x => account.Usage?.State != GitHubAccountQuotaState.Limited || IsActiveState(x.State))
                .Select(x => new CodespaceCandidate(account, x));
    }

    private async Task EnsureForkAsync(GitHubAccount account, string token, CancellationToken cancellationToken)
    {
        var repositoryName = _options.CodespaceRepositoryName;
        if (await github.RepositoryExistsAsync(token, account.Username, repositoryName, cancellationToken))
        {
            return;
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.repository.fork.requested",
            OperationalEventSeverity.Information,
            "Forking the proxy repository for the selected GitHub account.",
            NodeId: account.Id,
            Details: new { account.Username, _options.CodespaceRepositoryOwner, repositoryName }),
            cancellationToken);

        await github.ForkRepositoryAsync(token, _options.CodespaceRepositoryOwner, repositoryName, cancellationToken);
        var stopAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (await github.RepositoryExistsAsync(token, account.Username, repositoryName, cancellationToken))
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.repository.fork.ready",
                    OperationalEventSeverity.Information,
                    "Proxy repository fork is ready.",
                    NodeId: account.Id,
                    Details: new { account.Username, Repository = $"{account.Username}/{repositoryName}" }),
                    cancellationToken);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new InvalidOperationException($"Repository fork {account.Username}/{repositoryName} was not ready in time.");
    }

    private async Task<IReadOnlyList<string>> StopExtraCodespacesAsync(
        IReadOnlyList<GitHubAccount> accounts,
        Guid selectedAccountId,
        string selectedCodespaceName,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        foreach (var account in accounts)
        {
            IReadOnlyList<CodespaceSnapshot> snapshots;
            try
            {
                snapshots = await codespaces.SyncAsync(account.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not sync {account.Username}: {ex.Message}");
                continue;
            }

            var repositoryFullName = $"{account.Username}/{_options.CodespaceRepositoryName}";
            foreach (var snapshot in snapshots
                         .Where(x => IsActiveState(x.State))
                         .Where(x => string.Equals(x.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase)))
            {
                if (account.Id == selectedAccountId &&
                    string.Equals(snapshot.Name, selectedCodespaceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var stopped = await StopAndConfirmAsync(account, snapshot, cancellationToken);
                    warnings.Add(stopped
                        ? $"Stopped extra Codespace {account.Username}/{snapshot.Name}."
                        : $"Stop requested for extra Codespace {account.Username}/{snapshot.Name}, but GitHub still reports it active.");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not stop extra Codespace {account.Username}/{snapshot.Name}: {ex.Message}");
                    await events.WriteAsync(new OperationalEventWrite(
                        "codespace_proxy.extra_stop.failed",
                        OperationalEventSeverity.Warning,
                        "Could not stop an extra Codespace.",
                        NodeId: account.Id,
                        StandardError: ex.Message,
                        Details: new { account.Username, snapshot.Name, snapshot.State }),
                        cancellationToken);
                }
            }
        }

        return warnings;
    }

    private async Task<bool> StopAndConfirmAsync(GitHubAccount account, CodespaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await codespaces.StopAsync(account.Id, snapshot.Name, cancellationToken);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var refreshed = await codespaces.RefreshCodespaceAsync(account.Id, snapshot.Name, cancellationToken);
            if (refreshed is null || !IsActiveState(refreshed.State))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.extra_stop.pending",
            OperationalEventSeverity.Warning,
            "Stop was requested for an extra Codespace, but GitHub still reports it active.",
            NodeId: account.Id,
            Details: new { account.Username, snapshot.Name, snapshot.State }),
            cancellationToken);
        return false;
    }

    private static CodespaceSnapshot? PickCodespace(IReadOnlyList<CodespaceSnapshot> snapshots, string repositoryFullName) =>
        snapshots
            .Where(x => string.Equals(x.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => IsActiveState(x.State) ? 0 : 1)
            .ThenByDescending(x => x.LastUsedAt ?? x.UpdatedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static int UsageRank(GitHubAccountQuotaState? state) =>
        state switch
        {
            GitHubAccountQuotaState.Healthy => 0,
            GitHubAccountQuotaState.Warning => 1,
            GitHubAccountQuotaState.Unknown => 2,
            GitHubAccountQuotaState.Unavailable => 3,
            GitHubAccountQuotaState.Limited => 4,
            null => 3,
            _ => 5
        };

    private static int CodespaceRank(string state) =>
        state switch
        {
            var value when IsActiveState(value) => 0,
            var value when IsStoppedState(value) => 1,
            _ => 2
        };

    private static bool IsActiveState(string state) =>
        state.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

    private static bool IsStoppedState(string state) =>
        state.Equals("Shutdown", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ShuttingDown", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Stopped", StringComparison.OrdinalIgnoreCase);

    private static bool IsReusableState(string state) =>
        IsActiveState(state) || IsStoppedState(state);

    private sealed record AccountCandidate(GitHubAccount Account, GitHubUsageResponse? Usage, IReadOnlyList<CodespaceSnapshot> Snapshots);

    private sealed record CodespaceCandidate(AccountCandidate Account, CodespaceSnapshot Codespace);
}

public sealed record CodespaceProxySelection(
    Guid AccountId,
    string Username,
    string CodespaceName,
    string RepositoryFullName,
    string? Warning);

public sealed record CodespaceProxySelectionResult(bool Succeeded, string Message, CodespaceProxySelection? Selection)
{
    public static CodespaceProxySelectionResult Ok(CodespaceProxySelection selection) =>
        new(true, "Selected a Codespace for the proxy.", selection);

    public static CodespaceProxySelectionResult Fail(string message) =>
        new(false, message, null);
}
