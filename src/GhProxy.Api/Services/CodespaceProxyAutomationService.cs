using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class CodespaceProxyAutomationService(
    AppDbContext db,
    GitHubCodespaceService codespaces,
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
        foreach (var account in accounts.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var usage = await codespaces.GetUsageAsync(account.Id, cancellationToken);
                if (usage.State == GitHubAccountQuotaState.Limited)
                {
                    await events.WriteAsync(new OperationalEventWrite(
                        "codespace_proxy.account.skipped_limited",
                        OperationalEventSeverity.Warning,
                        "Skipped GitHub account because Codespaces quota is limited.",
                        NodeId: account.Id,
                        Details: new { account.Username, usage.Message }),
                        cancellationToken);
                    continue;
                }

                candidates.Add(new AccountCandidate(account, usage));
            }
            catch (Exception ex) when (ex is GitHubApiException or InvalidOperationException)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.account.usage_failed",
                    OperationalEventSeverity.Warning,
                    "Could not read Codespaces usage for GitHub account.",
                    NodeId: account.Id,
                    StandardError: ex.Message,
                    Details: new { account.Username }),
                    cancellationToken);
            }
        }

        var selectedAccount = candidates
            .OrderBy(x => UsageRank(x.Usage.State))
            .ThenBy(x => x.Usage.Quantity ?? decimal.MaxValue)
            .ThenBy(x => x.Account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selectedAccount is null)
        {
            return CodespaceProxySelectionResult.Fail("No GitHub account with usable Codespaces quota is available.");
        }

        var token = secrets.Unprotect(selectedAccount.Account.ProtectedPersonalAccessToken);
        await EnsureForkAsync(selectedAccount.Account, token, cancellationToken);
        var snapshots = await codespaces.SyncAsync(selectedAccount.Account.Id, cancellationToken);
        var repositoryFullName = $"{selectedAccount.Account.Username}/{_options.CodespaceRepositoryName}";
        var selectedCodespace = PickCodespace(snapshots, repositoryFullName);
        if (selectedCodespace is null)
        {
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

        var warnings = await StopExtraCodespacesAsync(accounts, selectedAccount.Account.Id, selectedCodespace.Name, cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.account.selected",
            OperationalEventSeverity.Information,
            "Selected GitHub account and Codespace for automatic proxy startup.",
            NodeId: selectedAccount.Account.Id,
            Details: new
            {
                selectedAccount.Account.Username,
                CodespaceName = selectedCodespace.Name,
                selectedCodespace.State,
                selectedAccount.Usage.Quantity,
                selectedAccount.Usage.UnitType,
                WarningCount = warnings.Count
            }),
            cancellationToken);

        return CodespaceProxySelectionResult.Ok(new CodespaceProxySelection(
            selectedAccount.Account.Id,
            selectedAccount.Account.Username,
            selectedCodespace.Name,
            repositoryFullName,
            warnings.Count == 0 ? null : string.Join(" ", warnings)));
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

            foreach (var snapshot in snapshots.Where(x => IsActiveState(x.State)))
            {
                if (account.Id == selectedAccountId &&
                    string.Equals(snapshot.Name, selectedCodespaceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    await codespaces.StopAsync(account.Id, snapshot.Name, cancellationToken);
                    warnings.Add($"Stopped extra Codespace {account.Username}/{snapshot.Name}.");
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

    private static CodespaceSnapshot? PickCodespace(IReadOnlyList<CodespaceSnapshot> snapshots, string repositoryFullName) =>
        snapshots
            .Where(x => string.Equals(x.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => IsActiveState(x.State) ? 0 : 1)
            .ThenByDescending(x => x.LastUsedAt ?? x.UpdatedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static int UsageRank(GitHubAccountQuotaState state) =>
        state switch
        {
            GitHubAccountQuotaState.Healthy => 0,
            GitHubAccountQuotaState.Warning => 1,
            GitHubAccountQuotaState.Unknown => 2,
            GitHubAccountQuotaState.Unavailable => 3,
            GitHubAccountQuotaState.Limited => 4,
            _ => 5
        };

    private static bool IsActiveState(string state) =>
        state.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

    private sealed record AccountCandidate(GitHubAccount Account, GitHubUsageResponse Usage);
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
