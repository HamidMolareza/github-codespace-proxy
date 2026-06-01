using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class LocalProxyRuntimeService(
    IServiceScopeFactory scopeFactory,
    ISecretProtector secrets,
    IClock clock,
    IOperationalEventSink events,
    IOptions<LocalProxyOptions> options,
    XrayConfigRenderer configRenderer,
    IXrayProcessRunner processRunner,
    ICommandRunner commandRunner,
    IRuntimeToolChecker toolChecker,
    IHostEnvironment environment,
    ILogger<LocalProxyRuntimeService> logger)
{
    private readonly LocalProxyOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _autoStartGate = new(1, 1);
    private readonly object _statusGate = new();
    private RunningLocalProxy? _running;
    private CodespaceRetryPlan? _retryPlan;
    private IdleWakePause? _idleWakePause;
    private LocalProxyAutomationRuntimeStatus _automationStatus = new("WaitingForTraffic", null, null, null, null, null, null);

    public Task<LocalProxyStartResult> StartAsync(Guid profileId, CancellationToken cancellationToken) =>
        StartInternalAsync(profileId, null, attachPublicListener: true, cancellationToken);

    public async Task<LocalProxyStartResult> StartCodespaceProxyAsync(Guid accountId, string codespaceName, Guid? profileId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.GitHubAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("GitHub account was not found.");
        var selectedProfileId = profileId ?? await GetOrCreateDefaultProfileIdAsync(db, cancellationToken);
        var token = secrets.Unprotect(account.ProtectedPersonalAccessToken);
        var toolCheck = toolChecker.CheckCodespaceProxyTools(_options.XrayExecutablePath);
        if (!toolCheck.Succeeded)
        {
            var message = $"Codespace proxy runtime is missing required tools: {toolCheck.MissingSummary}.";
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.preflight.failed",
                OperationalEventSeverity.Error,
                message,
                NodeId: accountId,
                Details: new { account.Username, MissingTools = toolCheck.Tools.Where(x => !x.Available).Select(x => x.Command).ToArray() }),
                cancellationToken);
            return LocalProxyStartResult.Fail(message, null);
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.direct_network.enabled",
            OperationalEventSeverity.Information,
            "GitHub Codespaces startup will bypass proxy environment variables and use the direct/VPN route.",
            NodeId: accountId,
            Details: new { account.Username, codespaceName, NoProxy = true }),
            cancellationToken);
        SetAutomationStatus("StartingCodespace", accountId, account.Username, codespaceName, null, null);

        var codespaces = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
        try
        {
            var beforeStart = await codespaces.RefreshCodespaceAsync(accountId, codespaceName, cancellationToken);
            var stopOnStartFailure = beforeStart is null || !IsCodespaceRunning(beforeStart.State);
            if (beforeStart is not null && IsCodespaceRunning(beforeStart.State))
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.codespace_start.skipped",
                    OperationalEventSeverity.Information,
                    "Codespace is already active; attaching the proxy without requesting another start.",
                    NodeId: accountId,
                    Details: new { account.Username, codespaceName, beforeStart.State }),
                    cancellationToken);
            }
            else
            {
                await codespaces.StartAsync(accountId, codespaceName, cancellationToken);
            }

            return await StartInternalAsync(
                selectedProfileId,
                new CodespaceProxyLaunch(accountId, account.Username, token, codespaceName, _options.CodespaceRemoteProxyPort, stopOnStartFailure),
                attachPublicListener: false,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ScheduleRetry(accountId, selectedProfileId, account.Username, codespaceName, ex.Message);
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.start.failed_before_runtime",
                OperationalEventSeverity.Error,
                "Codespace proxy startup failed before the local runtime was ready. A retry was scheduled.",
                NodeId: accountId,
                StandardError: ex.Message,
                Details: new { account.Username, codespaceName }),
                cancellationToken);
            return LocalProxyStartResult.Fail($"Failed to start Codespace proxy: {ex.Message}", null);
        }
    }

    public async Task<LocalProxyStartResult> EnsureAutomaticCodespaceProxyAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is { IsStopped: false, AccountId: not null })
        {
            return LocalProxyStartResult.Ok("Codespace-backed proxy is already running.", await GetActiveAsync(cancellationToken));
        }

        await _autoStartGate.WaitAsync(cancellationToken);
        try
        {
            running = _running;
            if (running is { IsStopped: false, AccountId: not null })
            {
                return LocalProxyStartResult.Ok("Codespace-backed proxy is already running.", await GetActiveAsync(cancellationToken));
            }

            if (running is { IsStopped: false })
            {
                await StopAsync("Stopped non-Codespace local proxy before automatic Codespace startup.", cancellationToken);
            }

            using var scope = scopeFactory.CreateScope();
            var automation = scope.ServiceProvider.GetRequiredService<CodespaceProxyAutomationService>();
            SetAutomationStatus("SelectingAccount", null, null, null, null, null);
            var selection = await automation.SelectAsync(cancellationToken);
            if (!selection.Succeeded || selection.Selection is null)
            {
                SetAutomationStatus("Error", null, null, null, null, selection.Message);
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.automation.selection_failed",
                    OperationalEventSeverity.Error,
                    selection.Message),
                    cancellationToken);
                return LocalProxyStartResult.Fail(selection.Message, null);
            }

            SetAutomationStatus("Selected", selection.Selection.AccountId, selection.Selection.Username, selection.Selection.CodespaceName, selection.Selection.Warning, null);
            return await StartCodespaceProxyAsync(selection.Selection.AccountId, selection.Selection.CodespaceName, null, cancellationToken);
        }
        finally
        {
            _autoStartGate.Release();
        }
    }

    public async Task<LocalProxyGatewayTarget> GetOrStartGatewayTargetAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped || running.AccountId is null)
        {
            var idleWake = await RecordIdleWakeRequestIfNeededAsync(cancellationToken);
            if (idleWake is { ShouldStart: false })
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.idle.wake_pending",
                    OperationalEventSeverity.Information,
                    idleWake.Message,
                    Details: new
                    {
                        idleWake.RequestCount,
                        idleWake.RequestThreshold,
                        idleWake.WindowExpiresAt
                    }),
                    cancellationToken);
                throw new LocalProxyIdleWakePendingException(idleWake.Message);
            }

            if (idleWake is { ShouldStart: true })
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.idle.wake_threshold_reached",
                    OperationalEventSeverity.Information,
                    idleWake.Message,
                    Details: new
                    {
                        idleWake.RequestCount,
                        idleWake.RequestThreshold,
                        idleWake.WindowExpiresAt
                    }),
                    cancellationToken);
            }

            var result = await EnsureAutomaticCodespaceProxyAsync(cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            running = _running;
        }

        if (running is null || running.IsStopped)
        {
            throw new InvalidOperationException("Codespace proxy runtime did not become available.");
        }

        return new LocalProxyGatewayTarget(
            running.InternalHttpPort,
            running.InternalSocksPort,
            running.SessionId,
            running.AccountId,
            running.CodespaceName);
    }

    public async Task RecordGatewayRelayActivityAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped)
        {
            return;
        }

        var now = clock.UtcNow;
        if (now - running.LastGatewayActivityPersistedAt < TimeSpan.FromSeconds(15))
        {
            running.LastActivityAt = now;
            return;
        }

        await RecordPublicActivityAsync(running, now, cancellationToken);
    }

    public LocalProxyAutomationRuntimeStatus GetAutomationStatus()
    {
        lock (_statusGate)
        {
            return _automationStatus;
        }
    }

    public async Task RecordGatewayRequestAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        RunningLocalProxy? running;
        lock (_statusGate)
        {
            _automationStatus = _automationStatus with { LastRequestAt = now };
            running = _running;
        }

        if (running is { IsStopped: false })
        {
            await RecordPublicRequestAsync(running, cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = (await db.LocalProxySessions.ToListAsync(cancellationToken))
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefault();
        if (session is null)
        {
            return;
        }

        session.LastRequestAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LocalProxyStartResult> RetryCodespaceProxyAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is { IsStopped: false, AccountId: not null })
        {
            return LocalProxyStartResult.Ok("Codespace-backed proxy is already running.", await GetActiveAsync(cancellationToken));
        }

        var status = GetAutomationStatus();
        if (IsStartingAutomationPhase(status.Phase))
        {
            return LocalProxyStartResult.Ok("Codespace proxy startup is already in progress.", await GetActiveAsync(cancellationToken));
        }

        CodespaceRetryPlan? retry;
        lock (_statusGate)
        {
            retry = _retryPlan;
        }

        if (retry is not null)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.retry.manual",
                OperationalEventSeverity.Information,
                "Manual Codespace proxy retry requested.",
                NodeId: retry.AccountId,
                Details: new { retry.CodespaceName, retry.Attempt }),
                cancellationToken);
            return await StartCodespaceProxyAsync(retry.AccountId, retry.CodespaceName, retry.ProfileId, cancellationToken);
        }

        var latest = await GetLatestCodespaceSessionAsync(failedOnly: false, cancellationToken);
        if (latest is not null)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.retry.manual",
                OperationalEventSeverity.Information,
                "Manual Codespace proxy retry requested from the latest saved session.",
                NodeId: latest.AccountId,
                Details: new { latest.CodespaceName, latest.ProfileId }),
                cancellationToken);
            return await StartCodespaceProxyAsync(latest.AccountId, latest.CodespaceName, latest.ProfileId, cancellationToken);
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.retry.manual",
            OperationalEventSeverity.Information,
            "Manual Codespace proxy retry requested; selecting an account and Codespace automatically."),
            cancellationToken);
        return await EnsureAutomaticCodespaceProxyAsync(cancellationToken);
    }

    public async Task RetryIfDueAsync(CancellationToken cancellationToken)
    {
        CodespaceRetryPlan? retry;
        lock (_statusGate)
        {
            retry = _retryPlan is not null && _retryPlan.NextRetryAt <= clock.UtcNow ? _retryPlan : null;
        }

        if (retry is null)
        {
            await SchedulePersistedFailureRetryAsync(cancellationToken);
            return;
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.retry.starting",
            OperationalEventSeverity.Information,
            "Retrying Codespace proxy startup.",
            NodeId: retry.AccountId,
            Details: new { retry.CodespaceName, retry.Attempt }),
            cancellationToken);
        await StartCodespaceProxyAsync(retry.AccountId, retry.CodespaceName, retry.ProfileId, cancellationToken);
    }

    private async Task SchedulePersistedFailureRetryAsync(CancellationToken cancellationToken)
    {
        if (_running is { IsStopped: false })
        {
            return;
        }

        lock (_statusGate)
        {
            if (_retryPlan is not null)
            {
                return;
            }
        }

        var latest = await GetLatestCodespaceSessionAsync(failedOnly: true, cancellationToken);
        if (latest is null)
        {
            return;
        }

        ScheduleRetry(
            latest.AccountId,
            latest.ProfileId,
            latest.AccountUsername,
            latest.CodespaceName,
            latest.LastError ?? "Codespace proxy is down.");
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.retry.scheduled_from_saved_failure",
            OperationalEventSeverity.Warning,
            "Scheduled Codespace proxy retry from the latest saved failed session.",
            NodeId: latest.AccountId,
            Details: new { latest.ProfileId, latest.CodespaceName, latest.LastError }),
            cancellationToken);
    }

    private async Task<IdleWakeDecision?> RecordIdleWakeRequestIfNeededAsync(CancellationToken cancellationToken)
    {
        var policy = GetIdleWakePolicy();
        if (policy.RequestThreshold <= 1)
        {
            return null;
        }

        IdleWakePause? pause;
        lock (_statusGate)
        {
            pause = _idleWakePause;
        }

        if (pause is null)
        {
            pause = await TryCreateIdleWakePauseFromLatestSessionAsync(policy, cancellationToken);
            if (pause is null)
            {
                return null;
            }
        }

        var now = clock.UtcNow;
        IdleWakeDecision decision;
        lock (_statusGate)
        {
            if (_running is { IsStopped: false })
            {
                return null;
            }

            pause = _idleWakePause ?? pause;
            if (now >= pause.WindowExpiresAt)
            {
                pause = pause with
                {
                    RequestCount = 0,
                    WindowStartedAt = now,
                    WindowExpiresAt = now + policy.Window
                };
            }

            var requestCount = pause.RequestCount + 1;
            if (requestCount >= policy.RequestThreshold)
            {
                _idleWakePause = null;
                _automationStatus = new(
                    "WaitingForTraffic",
                    pause.AccountId,
                    pause.AccountUsername,
                    pause.CodespaceName,
                    _automationStatus.Warning,
                    null,
                    null,
                    now);
                decision = new(
                    ShouldStart: true,
                    RequestCount: requestCount,
                    RequestThreshold: policy.RequestThreshold,
                    WindowExpiresAt: pause.WindowExpiresAt,
                    Message: $"Idle wake threshold reached with {requestCount}/{policy.RequestThreshold} requests; starting the Codespace proxy.");
            }
            else
            {
                pause = pause with { RequestCount = requestCount };
                _idleWakePause = pause;
                var remaining = policy.RequestThreshold - requestCount;
                var warning = $"Idle-paused after auto-stop. {remaining} more proxy request(s) within {Math.Max(0, (int)Math.Ceiling((pause.WindowExpiresAt - now).TotalSeconds))} seconds will restart the Codespace.";
                _automationStatus = new(
                    "ZzzIdle",
                    pause.AccountId,
                    pause.AccountUsername,
                    pause.CodespaceName,
                    warning,
                    null,
                    null,
                    now,
                    IdleWakePaused: true,
                    IdleWakeRequestCount: requestCount,
                    IdleWakeRequestThreshold: policy.RequestThreshold,
                    IdleWakeWindowExpiresAt: pause.WindowExpiresAt);
                decision = new(
                    ShouldStart: false,
                    RequestCount: requestCount,
                    RequestThreshold: policy.RequestThreshold,
                    WindowExpiresAt: pause.WindowExpiresAt,
                    Message: warning);
            }
        }

        await PersistLatestRequestAtAsync(now, cancellationToken);
        return decision;
    }

    private async Task<IdleWakePause?> TryCreateIdleWakePauseFromLatestSessionAsync(IdleWakePolicy policy, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var latest = (await db.LocalProxySessions
            .Include(x => x.Profile)
            .AsNoTracking()
            .Where(x => x.AccountId != null && x.CodespaceName != null && x.CodespaceName != "")
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefault();
        if (latest?.Profile is null || latest.AccountId is null || !IsIdleStoppedSession(latest, latest.Profile))
        {
            return null;
        }

        var username = await db.GitHubAccounts
            .AsNoTracking()
            .Where(x => x.Id == latest.AccountId.Value)
            .Select(x => x.Username)
            .FirstOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;
        var pause = new IdleWakePause(
            latest.ProfileId,
            latest.AccountId.Value,
            username,
            latest.CodespaceName,
            0,
            now,
            now + policy.Window);

        lock (_statusGate)
        {
            _idleWakePause ??= pause;
            _automationStatus = new(
                "ZzzIdle",
                latest.AccountId.Value,
                username,
                latest.CodespaceName,
                $"Idle-paused after auto-stop. {policy.RequestThreshold} proxy requests within {(int)policy.Window.TotalSeconds} seconds will restart the Codespace.",
                null,
                null,
                _automationStatus.LastRequestAt,
                IdleWakePaused: true,
                IdleWakeRequestCount: 0,
                IdleWakeRequestThreshold: policy.RequestThreshold,
                IdleWakeWindowExpiresAt: pause.WindowExpiresAt);
            return _idleWakePause;
        }
    }

    private async Task PersistLatestRequestAtAsync(DateTimeOffset requestAt, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = (await db.LocalProxySessions.ToListAsync(cancellationToken))
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefault();
        if (session is null)
        {
            return;
        }

        session.LastRequestAt = requestAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private IdleWakePolicy GetIdleWakePolicy()
    {
        var requestThreshold = Math.Clamp(_options.IdleWakeRequestThreshold, 1, 20);
        var windowSeconds = Math.Clamp(_options.IdleWakeWindowSeconds, 5, 3600);
        return new IdleWakePolicy(requestThreshold, TimeSpan.FromSeconds(windowSeconds));
    }

    private void SetIdleWakePauseAfterIdleStop(RunningLocalProxy running, string reason)
    {
        var policy = GetIdleWakePolicy();
        var now = clock.UtcNow;
        var windowExpiresAt = now + policy.Window;
        lock (_statusGate)
        {
            _idleWakePause = policy.RequestThreshold <= 1
                ? null
                : new IdleWakePause(
                    running.ProfileId,
                    running.AccountId,
                    running.AccountUsername,
                    running.CodespaceName,
                    0,
                    now,
                    windowExpiresAt);
            _automationStatus = new(
                "ZzzIdle",
                running.AccountId,
                running.AccountUsername,
                running.CodespaceName,
                reason,
                null,
                null,
                _automationStatus.LastRequestAt,
                IdleWakePaused: policy.RequestThreshold > 1,
                IdleWakeRequestCount: 0,
                IdleWakeRequestThreshold: policy.RequestThreshold,
                IdleWakeWindowExpiresAt: policy.RequestThreshold > 1 ? windowExpiresAt : null);
        }
    }

    private void SetAutomationStatus(
        string phase,
        Guid? accountId,
        string? username,
        string? codespaceName,
        string? warning,
        string? error,
        DateTimeOffset? nextRetryAt = null)
    {
        lock (_statusGate)
        {
            _idleWakePause = null;
            _automationStatus = new(phase, accountId, username, codespaceName, warning, nextRetryAt, error, _automationStatus.LastRequestAt);
        }
    }

    private void ClearRetry()
    {
        lock (_statusGate)
        {
            _retryPlan = null;
        }
    }

    private void ScheduleRetry(Guid accountId, Guid profileId, string? username, string codespaceName, string error)
    {
        var initialSeconds = Math.Clamp(_options.CodespaceRetryInitialSeconds, 5, 3600);
        var maxSeconds = Math.Clamp(_options.CodespaceRetryMaxSeconds, initialSeconds, 86400);
        lock (_statusGate)
        {
            _idleWakePause = null;
            var nextAttempt = _retryPlan is not null &&
                              _retryPlan.AccountId == accountId &&
                              string.Equals(_retryPlan.CodespaceName, codespaceName, StringComparison.OrdinalIgnoreCase)
                ? _retryPlan.Attempt + 1
                : 1;
            var retrySeconds = Math.Min(maxSeconds, initialSeconds * (int)Math.Pow(2, Math.Min(nextAttempt - 1, 8)));
            var retryAt = clock.UtcNow.AddSeconds(retrySeconds);
            _retryPlan = new CodespaceRetryPlan(accountId, profileId, username, codespaceName, nextAttempt, retryAt, error);
            _automationStatus = new("Retrying", accountId, username, codespaceName, _automationStatus.Warning, retryAt, error, _automationStatus.LastRequestAt);
        }
    }

    private void ScheduleRetry(CodespaceProxyLaunch launch, Guid profileId, string error) =>
        ScheduleRetry(launch.AccountId, profileId, launch.Username, launch.CodespaceName, error);

    private async Task<LocalProxyStartResult> StartInternalAsync(Guid profileId, CodespaceProxyLaunch? codespace, bool attachPublicListener, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_running is { IsStopped: false } running)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.start.reused",
                    OperationalEventSeverity.Warning,
                    "A Codespace proxy session is already running.",
                    Details: new { running.SessionId, running.HttpPort, running.SocksPort, running.CodespaceName }),
                    cancellationToken);
                return LocalProxyStartResult.Ok("A Codespace proxy session is already running.", await GetActiveAsync(cancellationToken));
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.LocalProxyProfiles.FirstOrDefaultAsync(x => x.Id == profileId, cancellationToken)
                ?? throw new InvalidOperationException("Local proxy profile was not found.");

            var listenerBindHost = string.IsNullOrWhiteSpace(_options.BindHostOverride)
                ? profile.BindHost
                : _options.BindHostOverride;
            var publicPort = profile.LocalPort;
            var portError = attachPublicListener ? TryGetUnavailablePortMessage(listenerBindHost, publicPort) : null;

            var session = new LocalProxySession
            {
                ProfileId = profile.Id,
                Status = LocalProxySessionStatus.Starting,
                BindHost = profile.BindHost,
                LocalPort = publicPort,
                SocksPort = publicPort,
                StartedAt = clock.UtcNow,
                LastActivityAt = clock.UtcNow,
                AccountId = codespace?.AccountId,
                CodespaceName = codespace?.CodespaceName,
                RemoteProxyPort = codespace?.RemoteProxyPort
            };

            db.LocalProxySessions.Add(session);
            profile.SocksPort = publicPort;
            profile.Status = LocalProxyProfileStatus.Starting;
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.start.requested",
                OperationalEventSeverity.Information,
                codespace is null
                    ? $"Starting one-port Xray proxy on {profile.BindHost}:{publicPort}."
                    : attachPublicListener
                        ? $"Starting Codespace-backed Xray proxy on {profile.BindHost}:{publicPort}."
                        : $"Starting Codespace-backed Xray proxy behind the gateway on {profile.BindHost}:{publicPort}.",
                Details: new { profile.Id, profile.BindHost, ListenerBindHost = listenerBindHost, PublicPort = publicPort, GatewayMode = !attachPublicListener, codespace?.CodespaceName, codespace?.RemoteProxyPort }),
                cancellationToken);

            if (portError is not null)
            {
                var failedAt = clock.UtcNow;
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = portError;
                session.StoppedAt = failedAt;
                session.LastActivityAt = failedAt;
                profile.Status = LocalProxyProfileStatus.Error;
                profile.UpdatedAt = failedAt;
                await db.SaveChangesAsync(cancellationToken);
                SetAutomationStatus("Error", codespace?.AccountId, codespace?.Username, codespace?.CodespaceName, _automationStatus.Warning, portError);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.port.unavailable",
                    OperationalEventSeverity.Error,
                    portError,
                    Details: new { profile.Id, PublicPort = publicPort }),
                    cancellationToken);
                return LocalProxyStartResult.Fail(portError, ToRuntimeState(session, profile, 0));
            }

            var internalHttpPort = GetFreePort();
            var internalSocksPort = GetFreePort();
            int? tunnelPort = codespace is null ? null : GetFreePort();
            Process? tunnelProcess = null;
            SshDirectTcpBridge? sshDirectBridge = null;
            MixedProxyListener? mixedListener = null;
            XrayProcessHandle? handle = null;

            try
            {
                if (codespace is not null && tunnelPort is not null)
                {
                    SetAutomationStatus("Connecting", codespace.AccountId, codespace.Username, codespace.CodespaceName, _automationStatus.Warning, null);
                    var tunnel = await StartCodespaceTunnelAsync(codespace, session.Id, tunnelPort.Value, cancellationToken);
                    tunnelProcess = tunnel.Process;
                    sshDirectBridge = tunnel.SshDirectBridge;
                }

                var password = string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword)
                    ? null
                    : secrets.Unprotect(profile.ProtectedProxyPassword);
                var runtimeDirectory = GetRuntimeDirectory(session.Id);
                Directory.CreateDirectory(runtimeDirectory);
                var configPath = Path.Combine(runtimeDirectory, "config.json");
                var accessLogPath = Path.Combine(runtimeDirectory, "access.log");
                var errorLogPath = Path.Combine(runtimeDirectory, "error.log");
                var upstream = tunnelPort is null ? null : new XrayOutboundProxy("http", "127.0.0.1", tunnelPort.Value);
                var config = configRenderer.Render(new XrayConfigRequest(
                    "127.0.0.1",
                    internalHttpPort,
                    internalSocksPort,
                    accessLogPath,
                    errorLogPath,
                    profile.ProxyUsername,
                    password,
                    upstream));
                await File.WriteAllTextAsync(configPath, config, cancellationToken);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.xray.config.rendered",
                    OperationalEventSeverity.Information,
                    upstream is null
                        ? "Rendered Xray local proxy configuration."
                        : "Rendered Xray configuration routed through the Codespace tunnel.",
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, ConfigPath = configPath, AccessLogPath = accessLogPath, ErrorLogPath = errorLogPath, internalHttpPort, internalSocksPort, tunnelPort }),
                    cancellationToken);

                handle = processRunner.Start(_options.XrayExecutablePath, configPath, runtimeDirectory);
                if (!await WaitForPortsAsync(internalHttpPort, internalSocksPort, handle, TimeSpan.FromSeconds(8), cancellationToken))
                {
                    var message = string.IsNullOrWhiteSpace(handle.StandardError)
                        ? "Xray did not open the hidden HTTP and SOCKS ports in time."
                        : $"Xray did not open the hidden ports: {handle.StandardError}";
                    throw new InvalidOperationException(message);
                }

                var state = new RunningLocalProxy(
                    profile.Id,
                    profile.Name,
                    session.Id,
                    profile.BindHost,
                    publicPort,
                    publicPort,
                    internalHttpPort,
                    internalSocksPort,
                    profile.ProxyUsername,
                    password,
                    Math.Max(1, profile.IdleShutdownMinutes),
                    handle,
                    mixedListener,
                    tunnelProcess,
                    sshDirectBridge,
                    configPath,
                    accessLogPath,
                    errorLogPath,
                    codespace?.AccountId,
                    codespace?.Username,
                    codespace?.CodespaceName,
                    codespace?.RemoteProxyPort,
                    tunnelPort);
                state.LastActivityAt = session.LastActivityAt;

                var readinessErrors = await ProbeRuntimeAsync(state, cancellationToken);
                if (readinessErrors.Count > 0)
                {
                    throw new InvalidOperationException($"Readiness probe failed: {string.Join(" ", readinessErrors)}");
                }

                if (attachPublicListener)
                {
                    mixedListener = MixedProxyListener.Start(
                        listenerBindHost,
                        publicPort,
                        internalHttpPort,
                        internalSocksPort,
                        logger,
                        ct => RecordPublicRequestAsync(state, ct));
                    state.AttachMixedListener(mixedListener);
                    if (!await WaitForPublicPortAsync(publicPort, TimeSpan.FromSeconds(4), cancellationToken))
                    {
                        throw new InvalidOperationException("Mixed HTTP/SOCKS listener did not open the public port in time.");
                    }
                }

                _running = state;
                _ = Task.Run(() => MonitorProcessAsync(state), CancellationToken.None);
                if (tunnelProcess is not null)
                {
                    _ = Task.Run(() => MonitorTunnelAsync(state), CancellationToken.None);
                }
                if (sshDirectBridge is not null)
                {
                    _ = Task.Run(() => MonitorSshDirectBridgeAsync(state), CancellationToken.None);
                }

                session.Status = LocalProxySessionStatus.Running;
                profile.Status = LocalProxyProfileStatus.Running;
                profile.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (codespace is not null)
                {
                    SetAutomationStatus("Running", codespace.AccountId, codespace.Username, codespace.CodespaceName, _automationStatus.Warning, null);
                }
                ClearRetry();

                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.xray.started",
                    OperationalEventSeverity.Information,
                    codespace is null
                    ? $"One-port Xray proxy is listening on {profile.BindHost}:{publicPort}."
                    : attachPublicListener
                        ? $"Codespace proxy is ready on {profile.BindHost}:{publicPort}."
                        : $"Codespace proxy backend is ready for gateway traffic on {profile.BindHost}:{publicPort}.",
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, handle.ProcessId, PublicPort = publicPort, GatewayMode = !attachPublicListener, internalHttpPort, internalSocksPort, tunnelPort, codespace?.CodespaceName }),
                    cancellationToken);

                return LocalProxyStartResult.Ok("Codespace-backed proxy is ready.", await ToResponseAsync(state, cancellationToken));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or SocketException)
            {
                if (mixedListener is not null)
                {
                    await mixedListener.DisposeAsync();
                }

                if (handle is not null)
                {
                    await handle.StopAsync();
                }

                await StopTunnelAsync(tunnelProcess, sshDirectBridge);
                var failedAt = clock.UtcNow;
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = ex.Message;
                session.StoppedAt = failedAt;
                session.LastActivityAt = failedAt;
                profile.Status = LocalProxyProfileStatus.Error;
                profile.UpdatedAt = failedAt;
                await db.SaveChangesAsync(cancellationToken);
                if (codespace is not null)
                {
                    ScheduleRetry(codespace, profile.Id, ex.Message);
                }
                else
                {
                    SetAutomationStatus("Error", null, null, null, _automationStatus.Warning, ex.Message);
                }
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.start.failed",
                    OperationalEventSeverity.Error,
                    "Failed to start the Codespace proxy.",
                    StandardError: ex.Message,
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, _options.XrayExecutablePath, codespace?.CodespaceName }),
                    cancellationToken);
                if (codespace?.StopCodespaceOnStartFailure == true)
                {
                    await StopCodespaceAfterStartFailureAsync(codespace, session.Id);
                }

                return LocalProxyStartResult.Fail($"Failed to start proxy: {ex.Message}", ToRuntimeState(session, profile, 0));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalProxyRuntimeResult> StopAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var running = _running;
            if (running is null || running.IsStopped)
            {
                ClearRetry();
                SetAutomationStatus("Stopped", null, null, null, reason, null);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.stop.none",
                    OperationalEventSeverity.Warning,
                    "Stop requested but no local Xray proxy session was running."),
                    cancellationToken);
                return new LocalProxyRuntimeResult(true, "No local proxy session is running.", null);
            }

            await RefreshRuntimeStatsAsync(running, cancellationToken);
            running.MarkStopped();
            if (running.MixedListener is not null)
            {
                await running.MixedListener.DisposeAsync();
            }

            await running.Process.StopAsync();
            await StopTunnelAsync(running.TunnelProcess, running.SshDirectBridge);
            await PersistStoppedAsync(running, reason, LocalProxySessionStatus.Stopped, cancellationToken);
            await StopBackingCodespaceAsync(running, reason, cancellationToken);
            if (IsIdleStopReason(reason))
            {
                SetIdleWakePauseAfterIdleStop(running, reason);
            }
            else
            {
                SetAutomationStatus("Stopped", running.AccountId, null, running.CodespaceName, reason, null);
            }
            ClearRetry();
            var response = await ToResponseAsync(running, cancellationToken);
            _running = null;
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.stop.completed",
                OperationalEventSeverity.Information,
                reason,
                Details: new { running.ProfileId, running.SessionId, running.HttpPort, running.SocksPort }),
                cancellationToken);
            return new LocalProxyRuntimeResult(true, reason, response);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalProxyRuntimeResult> ProbeActiveAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped)
        {
            return new LocalProxyRuntimeResult(false, "No local proxy session is running.", null);
        }

        var errors = await ProbeRuntimeAsync(running, cancellationToken);

        await RefreshRuntimeStatsAsync(running, cancellationToken);
        if (errors.Count == 0)
        {
            await SetLastErrorAsync(running, null, cancellationToken);
            return new LocalProxyRuntimeResult(true, BuildProbeSuccessMessage(), await ToResponseAsync(running, cancellationToken));
        }

        var message = string.Join(" ", errors);
        await SetLastErrorAsync(running, message, cancellationToken);
        await MarkRunningFailedAsync(running, $"Local Xray proxy probe failed: {message}", cancellationToken);
        return new LocalProxyRuntimeResult(false, $"Local Xray proxy probe failed: {message}", await ToResponseAsync(running, cancellationToken));
    }

    private async Task<IReadOnlyList<string>> ProbeRuntimeAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (_options.RequireHttpProbe)
        {
            try
            {
                await ProbeHttpAsync(running, cancellationToken);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.probe.http.success",
                    OperationalEventSeverity.Information,
                    "Local Xray HTTP proxy probe succeeded.",
                    Details: new { running.ProfileId, running.SessionId, running.ProbeHttpPort }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"HTTP: {ex.Message}");
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.probe.http.failure",
                    OperationalEventSeverity.Error,
                    "Local Xray HTTP proxy probe failed.",
                    StandardError: ex.Message,
                    Details: new { running.ProfileId, running.SessionId, running.ProbeHttpPort }),
                    cancellationToken);
            }
        }

        if (_options.RequireSocksProbe)
        {
            try
            {
                await ProbeSocksAsync(running, cancellationToken);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.probe.socks.success",
                    OperationalEventSeverity.Information,
                    "Local Xray SOCKS proxy probe succeeded.",
                    Details: new { running.ProfileId, running.SessionId, running.ProbeSocksPort }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"SOCKS: {ex.Message}");
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.probe.socks.failure",
                    OperationalEventSeverity.Error,
                    "Local Xray SOCKS proxy probe failed.",
                    StandardError: ex.Message,
                    Details: new { running.ProfileId, running.SessionId, running.ProbeSocksPort }),
                    cancellationToken);
            }
        }

        return errors;
    }

    private string BuildProbeSuccessMessage()
    {
        return (_options.RequireHttpProbe, _options.RequireSocksProbe) switch
        {
            (true, true) => "Local Xray HTTP and SOCKS probes succeeded.",
            (true, false) => "Local Xray HTTP probe succeeded.",
            (false, true) => "Local Xray SOCKS probe succeeded.",
            _ => "Local Xray readiness probes are disabled.",
        };
    }

    public async Task<LocalProxyRuntimeState?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped)
        {
            return null;
        }

        await RefreshRuntimeStatsAsync(running, cancellationToken);
        return await ToResponseAsync(running, cancellationToken);
    }

    public async Task StopIfIdleAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped)
        {
            return;
        }

        await RefreshRuntimeStatsAsync(running, cancellationToken);
        var idleShutdownMinutes = await GetCurrentIdleShutdownMinutesAsync(running, cancellationToken);
        var idleFor = clock.UtcNow - running.LastActivityAt;
        if (idleFor < TimeSpan.FromMinutes(idleShutdownMinutes))
        {
            return;
        }

        await StopAsync($"Stopped after {idleFor.TotalMinutes:n1} idle minutes.", cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "local_proxy.idle.timeout",
            OperationalEventSeverity.Information,
            "Stopped local Xray proxy after idle timeout.",
            Details: new { running.ProfileId, running.SessionId, IdleMinutes = idleFor.TotalMinutes, IdleShutdownMinutes = idleShutdownMinutes }),
            cancellationToken);
    }

    private async Task<int> GetCurrentIdleShutdownMinutesAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var idleShutdownMinutes = await db.LocalProxyProfiles
            .AsNoTracking()
            .Where(x => x.Id == running.ProfileId)
            .Select(x => (int?)x.IdleShutdownMinutes)
            .FirstOrDefaultAsync(cancellationToken);
        return Math.Max(1, idleShutdownMinutes ?? running.IdleShutdownMinutes);
    }

    private async Task MonitorProcessAsync(RunningLocalProxy running)
    {
        try
        {
            await running.Process.Completion;
            if (running.IsStopped)
            {
                return;
            }

            running.MarkStopped();
            await SetLastErrorAsync(running, running.Process.StandardError, CancellationToken.None);
            await PersistStoppedAsync(running, "Xray process exited unexpectedly.", LocalProxySessionStatus.Error, CancellationToken.None);
            if (running.MixedListener is not null)
            {
                await running.MixedListener.DisposeAsync();
            }

            await StopTunnelAsync(running.TunnelProcess, running.SshDirectBridge);
            ScheduleRetryIfCodespaceRuntime(running, "Xray process exited unexpectedly.");
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.xray.exited",
                OperationalEventSeverity.Error,
                "Xray process exited unexpectedly.",
                StandardError: running.Process.StandardError,
                Details: new { running.ProfileId, running.SessionId, running.Process.ProcessId }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to monitor Xray process.");
        }
    }

    private async Task MonitorTunnelAsync(RunningLocalProxy running)
    {
        var tunnelProcess = running.TunnelProcess;
        if (tunnelProcess is null)
        {
            return;
        }

        try
        {
            await tunnelProcess.WaitForExitAsync();
            if (running.IsStopped)
            {
                return;
            }

            running.MarkStopped();
            await SetLastErrorAsync(running, "Codespace SSH tunnel exited unexpectedly.", CancellationToken.None);
            await PersistStoppedAsync(running, "Codespace SSH tunnel exited unexpectedly.", LocalProxySessionStatus.Error, CancellationToken.None);
            if (running.MixedListener is not null)
            {
                await running.MixedListener.DisposeAsync();
            }

            await running.Process.StopAsync();
            ScheduleRetryIfCodespaceRuntime(running, "Codespace tunnel exited unexpectedly.");
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }

            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.tunnel.exited",
                OperationalEventSeverity.Error,
                "Codespace SSH tunnel exited unexpectedly.",
                Details: new { running.ProfileId, running.SessionId, running.CodespaceName, tunnelProcess.Id, tunnelProcess.ExitCode }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to monitor Codespace SSH tunnel process.");
        }
    }

    private async Task MonitorSshDirectBridgeAsync(RunningLocalProxy running)
    {
        var bridge = running.SshDirectBridge;
        if (bridge is null)
        {
            return;
        }

        try
        {
            await bridge.Completion;
            if (running.IsStopped)
            {
                return;
            }

            running.MarkStopped();
            await SetLastErrorAsync(running, "Codespace ssh -W bridge exited unexpectedly.", CancellationToken.None);
            await PersistStoppedAsync(running, "Codespace ssh -W bridge exited unexpectedly.", LocalProxySessionStatus.Error, CancellationToken.None);
            if (running.MixedListener is not null)
            {
                await running.MixedListener.DisposeAsync();
            }

            await running.Process.StopAsync();
            ScheduleRetryIfCodespaceRuntime(running, "Codespace ssh -W bridge exited unexpectedly.");
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }

            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.ssh_direct_bridge.exited",
                OperationalEventSeverity.Error,
                "Codespace ssh -W bridge exited unexpectedly.",
                Details: new { running.ProfileId, running.SessionId, running.CodespaceName, running.LocalTunnelPort }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to monitor Codespace ssh -W bridge.");
        }
    }

    private void ScheduleRetryIfCodespaceRuntime(RunningLocalProxy running, string error)
    {
        if (running.AccountId is not null && !string.IsNullOrWhiteSpace(running.CodespaceName))
        {
            ScheduleRetry(running.AccountId.Value, running.ProfileId, running.AccountUsername, running.CodespaceName, error);
            return;
        }

        SetAutomationStatus("Error", running.AccountId, running.AccountUsername, running.CodespaceName, null, error);
    }

    private async Task<SavedCodespaceProxySession?> GetLatestCodespaceSessionAsync(bool failedOnly, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessions = await db.LocalProxySessions
            .AsNoTracking()
            .Where(x => x.AccountId != null && x.CodespaceName != null && x.CodespaceName != "")
            .ToListAsync(cancellationToken);
        var latest = sessions.OrderByDescending(x => x.StartedAt).FirstOrDefault();
        if (failedOnly && latest?.Status != LocalProxySessionStatus.Error)
        {
            return null;
        }

        if (latest?.AccountId is null || string.IsNullOrWhiteSpace(latest.CodespaceName))
        {
            return null;
        }

        var username = await db.GitHubAccounts
            .AsNoTracking()
            .Where(x => x.Id == latest.AccountId.Value)
            .Select(x => x.Username)
            .FirstOrDefaultAsync(cancellationToken);
        return new SavedCodespaceProxySession(
            latest.ProfileId,
            latest.AccountId.Value,
            username,
            latest.CodespaceName,
            latest.LastError);
    }

    private async Task MarkRunningFailedAsync(RunningLocalProxy running, string reason, CancellationToken cancellationToken)
    {
        running.MarkStopped();
        if (running.MixedListener is not null)
        {
            await running.MixedListener.DisposeAsync();
        }

        await running.Process.StopAsync();
        await StopTunnelAsync(running.TunnelProcess, running.SshDirectBridge);
        await PersistStoppedAsync(running, reason, LocalProxySessionStatus.Error, cancellationToken);
        ScheduleRetryIfCodespaceRuntime(running, reason);
        if (ReferenceEquals(_running, running))
        {
            _running = null;
        }
    }

    private async Task ProbeHttpAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{running.ProbeHttpPort}"),
            UseProxy = true
        };
        if (running.RequiresAuthentication)
        {
            handler.Proxy.Credentials = new NetworkCredential(running.ProxyUsername, running.ProxyPassword);
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.ProbeTimeoutSeconds, 5, 120))
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ProbeUrl);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ProbeSocksAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        var uri = new Uri(_options.ProbeUrl);
        var port = uri.Port > 0 ? uri.Port : uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, running.ProbeSocksPort, cancellationToken);
        await using var stream = client.GetStream();

        var methods = running.RequiresAuthentication
            ? new byte[] { 0x05, 0x01, 0x02 }
            : [0x05, 0x01, 0x00];
        await stream.WriteAsync(methods, cancellationToken);
        var selection = await ReadExactAsync(stream, 2, cancellationToken);
        if (selection[0] != 0x05 || selection[1] == 0xff)
        {
            throw new InvalidOperationException("SOCKS server rejected authentication methods.");
        }

        if (selection[1] == 0x02)
        {
            await WriteSocksUserPasswordAsync(stream, running.ProxyUsername!, running.ProxyPassword!, cancellationToken);
            var auth = await ReadExactAsync(stream, 2, cancellationToken);
            if (auth[1] != 0x00)
            {
                throw new InvalidOperationException("SOCKS username/password authentication failed.");
            }
        }

        var hostBytes = Encoding.ASCII.GetBytes(uri.Host);
        if (hostBytes.Length > 255)
        {
            throw new InvalidOperationException("SOCKS probe host name is too long.");
        }

        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        Array.Copy(hostBytes, 0, request, 5, hostBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xff);
        await stream.WriteAsync(request, cancellationToken);

        var responseHead = await ReadExactAsync(stream, 4, cancellationToken);
        if (responseHead[0] != 0x05 || responseHead[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS connect failed with code {responseHead[1]}.");
        }

        var addressLength = responseHead[3] switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => throw new InvalidOperationException("SOCKS server returned an unsupported address type.")
        };
        await ReadExactAsync(stream, addressLength + 2, cancellationToken);
    }

    private static async Task WriteSocksUserPasswordAsync(Stream stream, string username, string password, CancellationToken cancellationToken)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        if (usernameBytes.Length > 255 || passwordBytes.Length > 255)
        {
            throw new InvalidOperationException("SOCKS username and password must be 255 bytes or shorter.");
        }

        var payload = new byte[3 + usernameBytes.Length + passwordBytes.Length];
        payload[0] = 0x01;
        payload[1] = (byte)usernameBytes.Length;
        Array.Copy(usernameBytes, 0, payload, 2, usernameBytes.Length);
        payload[2 + usernameBytes.Length] = (byte)passwordBytes.Length;
        Array.Copy(passwordBytes, 0, payload, 3 + usernameBytes.Length, passwordBytes.Length);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Unexpected end of SOCKS response.");
            }

            offset += read;
        }

        return buffer;
    }

    private async Task<bool> WaitForPortsAsync(int httpPort, int socksPort, XrayProcessHandle process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt && !process.HasExited)
        {
            if (await CanConnectAsync(httpPort, cancellationToken) &&
                await CanConnectAsync(socksPort, cancellationToken))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForPublicPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (await CanConnectAsync(port, cancellationToken))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CodespaceTunnelHandle> StartCodespaceTunnelAsync(CodespaceProxyLaunch launch, Guid sessionId, int localTunnelPort, CancellationToken cancellationToken)
    {
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.starting",
            OperationalEventSeverity.Information,
            "Waiting for Codespace SSH access.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { launch.Username, launch.CodespaceName, launch.RemoteProxyPort, LocalTunnelPort = localTunnelPort }),
            cancellationToken);

        if (_options.CodespaceRequireSshReady)
        {
            await RunRequiredWithRetriesAsync(
                "codespace.ssh.ready",
                ["codespace", "ssh", "-c", launch.CodespaceName, "true"],
                launch,
                sessionId,
                TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceStartTimeoutSeconds, 30, 600)),
                cancellationToken);
        }
        else
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.ssh_ready.skipped",
                OperationalEventSeverity.Information,
                "Skipped Codespace SSH readiness command; opening the port-forward tunnel directly.",
                NodeId: launch.AccountId,
                SessionId: sessionId,
                Details: new { launch.CodespaceName }),
                cancellationToken);
        }

        if (_options.CodespaceEnsureRemoteProxy)
        {
            SetAutomationStatus("EnsuringRemoteProxy", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
            await EnsureCodespaceRemoteProxyAsync(launch, sessionId, cancellationToken);
        }
        else
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.remote_proxy.ensure.skipped",
                OperationalEventSeverity.Information,
                "Skipped remote proxy ensure step; using gh codespace ports forward like sp-proxy.",
                NodeId: launch.AccountId,
                SessionId: sessionId,
                Details: new { launch.CodespaceName, launch.RemoteProxyPort }),
                cancellationToken);
        }

        if (UseSshDirectBridgeTunnel())
        {
            return await StartSshDirectBridgeTunnelAsync(launch, sessionId, localTunnelPort, cancellationToken);
        }

        if (UseNativeSshTunnel())
        {
            return await StartNativeSshTunnelAsync(launch, sessionId, localTunnelPort, cancellationToken);
        }

        return await StartPortsForwardTunnelAsync(launch, sessionId, localTunnelPort, cancellationToken);
    }

    private async Task<CodespaceTunnelHandle> StartPortsForwardTunnelAsync(CodespaceProxyLaunch launch, Guid sessionId, int localTunnelPort, CancellationToken cancellationToken)
    {
        SetAutomationStatus("OpeningTunnel", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
        var command = new CommandSpec(
            "gh",
            [
                "codespace",
                "ports",
                "forward",
                $"{launch.RemoteProxyPort}:{localTunnelPort}",
                "-c",
                launch.CodespaceName
            ],
            Kind: "codespace.ports.forward",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            EnvironmentVariables: DirectNetworkEnvironment.CreateGitHubCommandEnvironment(launch.Token));

        var process = await StartTunnelProcessWithRetriesAsync(
            command,
            launch,
            sessionId,
            localTunnelPort,
            "gh codespace ports forward",
            cancellationToken);
        return new CodespaceTunnelHandle(process, null);
    }

    private async Task<CodespaceTunnelHandle> StartNativeSshTunnelAsync(CodespaceProxyLaunch launch, Guid sessionId, int localTunnelPort, CancellationToken cancellationToken)
    {
        SetAutomationStatus("OpeningNativeSshTunnel", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
        var (configPath, host) = await GenerateCodespaceSshConfigAsync(launch, sessionId, cancellationToken);

        var command = new CommandSpec(
            "ssh",
            [
                "-F",
                configPath,
                "-N",
                "-L",
                $"127.0.0.1:{localTunnelPort}:127.0.0.1:{launch.RemoteProxyPort}",
                "-o",
                "ExitOnForwardFailure=yes",
                "-o",
                "ServerAliveInterval=10",
                "-o",
                "ServerAliveCountMax=3",
                "-o",
                "TCPKeepAlive=yes",
                "-o",
                "BatchMode=yes",
                host
            ],
            Kind: "codespace.ssh.forward",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            EnvironmentVariables: DirectNetworkEnvironment.CreateGitHubCommandEnvironment(launch.Token));

        var process = await StartTunnelProcessWithRetriesAsync(
            command,
            launch,
            sessionId,
            localTunnelPort,
            "native ssh -L",
            cancellationToken);
        return new CodespaceTunnelHandle(process, null);
    }

    private async Task<CodespaceTunnelHandle> StartSshDirectBridgeTunnelAsync(CodespaceProxyLaunch launch, Guid sessionId, int localTunnelPort, CancellationToken cancellationToken)
    {
        SetAutomationStatus("OpeningSshDirectBridge", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
        var (configPath, host) = await GenerateCodespaceSshConfigAsync(launch, sessionId, cancellationToken);
        var bridge = SshDirectTcpBridge.Start(
            localTunnelPort,
            configPath,
            host,
            $"127.0.0.1:{launch.RemoteProxyPort}",
            launch.Token,
            DirectNetworkEnvironment.CreateGitHubCommandEnvironment(launch.Token),
            logger);

        try
        {
            await WaitForSshDirectBridgeAsync(localTunnelPort, launch, sessionId, cancellationToken);
            SetAutomationStatus("TunnelReady", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.tunnel.ready",
                OperationalEventSeverity.Information,
                "Codespace ssh -W bridge is ready.",
                NodeId: launch.AccountId,
                SessionId: sessionId,
                Details: new { launch.CodespaceName, LocalTunnelPort = localTunnelPort, launch.RemoteProxyPort, TunnelKind = "ssh -W bridge" }),
                cancellationToken);
            return new CodespaceTunnelHandle(null, bridge);
        }
        catch
        {
            await bridge.DisposeAsync();
            throw;
        }
    }

    private async Task<(string ConfigPath, string Host)> GenerateCodespaceSshConfigAsync(CodespaceProxyLaunch launch, Guid sessionId, CancellationToken cancellationToken)
    {
        var runtimeDirectory = GetRuntimeDirectory(sessionId);
        Directory.CreateDirectory(runtimeDirectory);
        var configPath = Path.Combine(runtimeDirectory, "codespace-ssh-config");
        var configResult = await RunRequiredWithRetriesAsync(
            "codespace.ssh.config",
            ["codespace", "ssh", "--config", "-c", launch.CodespaceName],
            launch,
            sessionId,
            TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceStartTimeoutSeconds, 30, 600)),
            cancellationToken);

        var config = configResult.StandardOutput;
        var host = TryGetFirstSshConfigHost(config)
            ?? throw new InvalidOperationException("gh codespace ssh --config did not return a usable Host entry.");
        var tokenEnvPath = Path.Combine(runtimeDirectory, "gh-token-env");
        await File.WriteAllTextAsync(tokenEnvPath, BuildGitHubTokenEnvFile(launch.Token), cancellationToken);
        TrySetOwnerOnlyReadWrite(tokenEnvPath);
        config = InjectProxyCommandAuth(config, tokenEnvPath);
        await File.WriteAllTextAsync(configPath, config, cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.ssh_config.generated",
            OperationalEventSeverity.Information,
            "Generated OpenSSH config for Codespace SSH tunnel.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { launch.CodespaceName, ConfigPath = configPath, SshHost = host }),
            cancellationToken);
        return (configPath, host);
    }

    private static string BuildGitHubTokenEnvFile(string token) =>
        string.Join('\n',
            [
                $"export GH_TOKEN={ShellQuote(token)}",
                $"export GITHUB_TOKEN={ShellQuote(token)}",
                "export GH_PROMPT_DISABLED=1",
                "export GH_NO_UPDATE_NOTIFIER=1",
                ""
            ]);

    internal static string InjectProxyCommandAuth(string config, string tokenEnvPath)
    {
        using var reader = new StringReader(config);
        var builder = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("ProxyCommand ", StringComparison.OrdinalIgnoreCase))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                var command = trimmed["ProxyCommand ".Length..].Trim();
                builder.Append(indent)
                    .Append("ProxyCommand sh -c ")
                    .Append(ShellQuote($". {ShellQuote(tokenEnvPath)}; exec {command}"))
                    .AppendLine();
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static void TrySetOwnerOnlyReadWrite(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // The token file is still in the private runtime directory if chmod is unavailable.
        }
    }

    private async Task<Process> StartTunnelProcessWithRetriesAsync(
        CommandSpec command,
        CodespaceProxyLaunch launch,
        Guid sessionId,
        int localTunnelPort,
        string tunnelKind,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceTunnelReadyTimeoutSeconds, 5, 120));
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var process = commandRunner.Start(command);
            var stopAt = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < stopAt)
            {
                if (process.HasExited)
                {
                    lastError = new InvalidOperationException($"{tunnelKind} exited before tunnel port {localTunnelPort} became ready.");
                    break;
                }

                if (await CanConnectAsync(localTunnelPort, cancellationToken))
                {
                    SetAutomationStatus("TunnelReady", launch.AccountId, launch.Username, launch.CodespaceName, GetAutomationStatus().Warning, null);
                    await events.WriteAsync(new OperationalEventWrite(
                        "codespace_proxy.tunnel.ready",
                        OperationalEventSeverity.Information,
                        "Codespace port-forward tunnel is ready.",
                        NodeId: launch.AccountId,
                        SessionId: sessionId,
                        Details: new { launch.CodespaceName, LocalTunnelPort = localTunnelPort, launch.RemoteProxyPort, process.Id, Attempt = attempt, TunnelKind = tunnelKind }),
                        cancellationToken);
                    return process;
                }

                await Task.Delay(250, cancellationToken);
            }

            StopProcess(process);
            lastError ??= new InvalidOperationException($"Timed out waiting for tunnel port {localTunnelPort}.");
            if (attempt < 3)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.tunnel.retry",
                    OperationalEventSeverity.Warning,
                    "Codespace port-forward tunnel was not ready; retrying.",
                    NodeId: launch.AccountId,
                    SessionId: sessionId,
                    StandardError: lastError.Message,
                    Details: new { launch.CodespaceName, LocalTunnelPort = localTunnelPort, launch.RemoteProxyPort, Attempt = attempt }),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                lastError = null;
            }
        }

        throw lastError ?? new InvalidOperationException($"Timed out waiting for tunnel port {localTunnelPort}.");
    }

    private async Task WaitForSshDirectBridgeAsync(int localTunnelPort, CodespaceProxyLaunch launch, Guid sessionId, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceTunnelReadyTimeoutSeconds, 5, 120));
        var stopAt = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, localTunnelPort, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
            {
                lastError = ex;
            }

            await Task.Delay(250, cancellationToken);
        }

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.tunnel.retry",
            OperationalEventSeverity.Warning,
            "Codespace ssh -W bridge was not ready.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            StandardError: lastError?.Message,
            Details: new { launch.CodespaceName, LocalTunnelPort = localTunnelPort, launch.RemoteProxyPort, TunnelKind = "ssh -W bridge" }),
            cancellationToken);
        throw lastError ?? new InvalidOperationException($"Timed out waiting for ssh -W bridge port {localTunnelPort}.");
    }

    private bool UseSshDirectBridgeTunnel() =>
        string.Equals(_options.CodespaceTunnelMode, "ssh-direct", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_options.CodespaceTunnelMode, "ssh-w", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_options.CodespaceTunnelMode, "direct-ssh", StringComparison.OrdinalIgnoreCase);

    private bool UseNativeSshTunnel() =>
        string.Equals(_options.CodespaceTunnelMode, "native-ssh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_options.CodespaceTunnelMode, "ssh", StringComparison.OrdinalIgnoreCase);

    internal static string? TryGetFirstSshConfigHost(string config)
    {
        using var reader = new StringReader(config);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hosts = trimmed[5..]
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var host = hosts.FirstOrDefault(candidate => candidate != "*" && !candidate.Contains('*', StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(host))
            {
                return host;
            }
        }

        return null;
    }

    private async Task EnsureCodespaceRemoteProxyAsync(CodespaceProxyLaunch launch, Guid sessionId, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceRemoteProxyStartupTimeoutSeconds, 45, 600));
        var dashboardPort = Math.Clamp(_options.CodespaceRemoteDashboardPort, 1, 65535);

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.remote_proxy.ensure.starting",
            OperationalEventSeverity.Information,
            "Ensuring the proxy2 process is listening inside the Codespace.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new
            {
                launch.Username,
                launch.CodespaceName,
                launch.RemoteProxyPort,
                RemoteDashboardPort = dashboardPort,
                RemoteProxyCommand = _options.CodespaceRemoteProxyCommand,
                TimeoutSeconds = (int)timeout.TotalSeconds
            }),
            cancellationToken);

        await RunRequiredWithRetriesAsync(
            "codespace.proxy.remote_ensure",
            ["codespace", "ssh", "-c", launch.CodespaceName, BuildRemoteProxyEnsureCommand(launch.RemoteProxyPort, dashboardPort)],
            launch,
            sessionId,
            timeout,
            cancellationToken);

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.remote_proxy.ready",
            OperationalEventSeverity.Information,
            "The proxy2 process is listening inside the Codespace.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { launch.CodespaceName, launch.RemoteProxyPort, RemoteDashboardPort = dashboardPort }),
            cancellationToken);
    }

    private string BuildRemoteProxyEnsureCommand(int proxyPort, int dashboardPort)
    {
        var proxyCommand = string.IsNullOrWhiteSpace(_options.CodespaceRemoteProxyCommand)
            ? "proxy"
            : _options.CodespaceRemoteProxyCommand.Trim();
        var script = string.Join('\n',
            "set -u",
            $"proxy_port={proxyPort}",
            $"dashboard_port={dashboardPort}",
            $"proxy_command={QuoteForBash(proxyCommand)}",
            "fixed_proxy=/tmp/proxy-local/proxy-fixed",
            "mkdir -p /tmp/proxy-local",
            "if ! command -v \"${proxy_command}\" >/dev/null 2>&1; then echo \"proxy command '${proxy_command}' was not found in the Codespace. Ensure this Codespace uses the proxy2 devcontainer image.\" >&2; exit 127; fi",
            "python3 - \"$(command -v \"${proxy_command}\")\" \"${fixed_proxy}\" <<'PY'",
            "import os",
            "import stat",
            "import sys",
            "from pathlib import Path",
            "",
            "source = Path(sys.argv[1])",
            "target = Path(sys.argv[2])",
            "text = source.read_text(encoding='utf-8')",
            "start_marker = 'def tunnel_bidirectional(left_socket, right_socket):\\n'",
            "end_marker = '\\n\\nclass ClientTracker:'",
            "start = text.find(start_marker)",
            "if start < 0:",
            "    raise SystemExit('proxy relay function was not found')",
            "end = text.find(end_marker, start)",
            "if end < 0:",
            "    raise SystemExit('proxy relay function end was not found')",
            "replacement = r'''def tunnel_bidirectional(left_socket, right_socket):",
            "    left_socket.setblocking(False)",
            "    right_socket.setblocking(False)",
            "    sockets = [left_socket, right_socket]",
            "    buffers = {",
            "        left_socket: bytearray(),",
            "        right_socket: bytearray(),",
            "    }",
            "    read_closed = {",
            "        left_socket: False,",
            "        right_socket: False,",
            "    }",
            "    write_shutdown = {",
            "        left_socket: False,",
            "        right_socket: False,",
            "    }",
            "    stats = {",
            "        \"left_to_right_bytes\": 0,",
            "        \"right_to_left_bytes\": 0,",
            "    }",
            "    max_pending_bytes = 2 * 1024 * 1024",
            "",
            "    def peer_for(sock):",
            "        return right_socket if sock is left_socket else left_socket",
            "",
            "    def is_retryable_socket_error(exc):",
            "        return isinstance(exc, (BlockingIOError, InterruptedError)) or getattr(exc, \"errno\", None) in {",
            "            errno.EAGAIN,",
            "            errno.EWOULDBLOCK,",
            "            errno.EINTR,",
            "        }",
            "",
            "    def shutdown_write(sock):",
            "        if write_shutdown[sock]:",
            "            return",
            "        write_shutdown[sock] = True",
            "        try:",
            "            sock.shutdown(socket.SHUT_WR)",
            "        except OSError:",
            "            pass",
            "",
            "    def shutdown_peer_when_drained(sock):",
            "        peer = peer_for(sock)",
            "        if read_closed[peer] and not buffers[sock]:",
            "            shutdown_write(sock)",
            "",
            "    while True:",
            "        if read_closed[left_socket] and read_closed[right_socket] and not buffers[left_socket] and not buffers[right_socket]:",
            "            return stats",
            "",
            "        readable = [",
            "            sock",
            "            for sock in sockets",
            "            if not read_closed[sock] and len(buffers[peer_for(sock)]) < max_pending_bytes",
            "        ]",
            "        writable = [sock for sock in sockets if buffers[sock] and not write_shutdown[sock]]",
            "        if not readable and not writable:",
            "            return stats",
            "",
            "        try:",
            "            ready_read, ready_write, exceptional = select.select(readable, writable, sockets, 1.0)",
            "        except OSError:",
            "            return stats",
            "        if exceptional:",
            "            return stats",
            "",
            "        for current in ready_read:",
            "            try:",
            "                data = current.recv(BUFFER_SIZE)",
            "            except OSError as exc:",
            "                if is_retryable_socket_error(exc):",
            "                    continue",
            "                read_closed[current] = True",
            "                shutdown_peer_when_drained(peer_for(current))",
            "                continue",
            "",
            "            if not data:",
            "                read_closed[current] = True",
            "                shutdown_peer_when_drained(peer_for(current))",
            "                continue",
            "",
            "            target = peer_for(current)",
            "            buffers[target].extend(data)",
            "            if current is left_socket:",
            "                stats[\"left_to_right_bytes\"] += len(data)",
            "            else:",
            "                stats[\"right_to_left_bytes\"] += len(data)",
            "",
            "        for current in ready_write:",
            "            if not buffers[current]:",
            "                continue",
            "            try:",
            "                sent = current.send(buffers[current])",
            "            except OSError as exc:",
            "                if is_retryable_socket_error(exc):",
            "                    continue",
            "                return stats",
            "            if sent <= 0:",
            "                return stats",
            "            del buffers[current][:sent]",
            "            shutdown_peer_when_drained(current)",
            "'''",
            "patched = text[:start] + replacement + text[end:]",
            "target.write_text(patched, encoding='utf-8')",
            "mode = source.stat().st_mode",
            "target.chmod(mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)",
            "PY",
            "if timeout 2 bash -lc \"</dev/tcp/127.0.0.1/${proxy_port}\"; then python3 - \"${proxy_port}\" <<'PY'",
            "import os",
            "import signal",
            "import sys",
            "import time",
            "",
            "port = sys.argv[1]",
            "for pid_text in os.listdir('/proc'):",
            "    if not pid_text.isdigit() or pid_text == str(os.getpid()):",
            "        continue",
            "    try:",
            "        raw = open(f'/proc/{pid_text}/cmdline', 'rb').read()",
            "    except OSError:",
            "        continue",
            "    args = [part.decode('utf-8', 'replace') for part in raw.split(b'\\0') if part]",
            "    if not args or '--mixed-port' not in args or port not in args:",
            "        continue",
            "    executable = os.path.basename(args[0])",
            "    script = os.path.basename(args[1]) if len(args) > 1 else ''",
            "    if executable not in {'proxy', 'proxy-fixed', 'python', 'python3'} and script not in {'proxy', 'proxy-fixed'}:",
            "        continue",
            "    os.kill(int(pid_text), signal.SIGTERM)",
            "time.sleep(1)",
            "PY",
            "fi",
            "nohup \"${fixed_proxy}\" --bind 127.0.0.1 --mixed-port \"${proxy_port}\" --dashboard-bind 127.0.0.1 --dashboard-port \"${dashboard_port}\" --usage-log-file /tmp/proxy-local/usage.log >/tmp/proxy-local/proxy.log 2>&1 &",
            "for _ in $(seq 1 30); do if timeout 1 bash -lc \"</dev/tcp/127.0.0.1/${proxy_port}\"; then echo \"remote proxy started on ${proxy_port}\"; exit 0; fi; sleep 1; done",
            "echo \"remote proxy did not listen on ${proxy_port}\" >&2",
            "tail -80 /tmp/proxy-local/proxy.log >&2 || true",
            "exit 1");

        return $"bash -lc {QuoteForBash(script)}";
    }

    private static string QuoteForBash(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private async Task<CommandResult> RunRequiredAsync(
        string kind,
        IReadOnlyList<string> arguments,
        CodespaceProxyLaunch launch,
        Guid sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(new CommandSpec(
            "gh",
            arguments,
            timeout,
            kind,
            launch.AccountId,
            sessionId,
            DirectNetworkEnvironment.CreateGitHubCommandEnvironment(launch.Token)), cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{kind} failed: {FirstNonEmpty(result.StandardError, result.StandardOutput, "command failed")}");
        }

        return result;
    }

    private async Task<CommandResult> RunRequiredWithRetriesAsync(
        string kind,
        IReadOnlyList<string> arguments,
        CodespaceProxyLaunch launch,
        Guid sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await RunRequiredAsync(kind, arguments, launch, sessionId, timeout, cancellationToken);
            }
            catch (InvalidOperationException ex) when (attempt < 3)
            {
                lastError = ex;
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.command.retry",
                    OperationalEventSeverity.Warning,
                    $"Command {kind} failed; retrying.",
                    NodeId: launch.AccountId,
                    SessionId: sessionId,
                    CommandKind: kind,
                    StandardError: ex.Message,
                    Details: new { launch.CodespaceName, Attempt = attempt }),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException($"{kind} failed.");
    }

    private async Task<Guid> GetOrCreateDefaultProfileIdAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var profiles = await db.LocalProxyProfiles.ToListAsync(cancellationToken);
        var profile = profiles.OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (profile is not null)
        {
            return profile.Id;
        }

        var now = clock.UtcNow;
        profile = new LocalProxyProfile
        {
            Name = "Default Codespace Proxy",
            BindHost = "127.0.0.1",
            LocalPort = 8910,
            SocksPort = 8910,
            IdleShutdownMinutes = Math.Clamp(_options.DefaultIdleShutdownMinutes, 1, 1440),
            Notes = "Default one-port Codespace-backed proxy profile.",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.LocalProxyProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static async Task StopTunnelAsync(Process? process, SshDirectTcpBridge? bridge)
    {
        StopProcess(process);
        if (bridge is not null)
        {
            await bridge.DisposeAsync();
        }
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Best-effort cleanup for managed tunnel processes.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Task RefreshRuntimeStatsAsync(RunningLocalProxy running, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task PersistStoppedAsync(RunningLocalProxy running, string reason, LocalProxySessionStatus status, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        var profile = await db.LocalProxyProfiles.FirstOrDefaultAsync(x => x.Id == running.ProfileId, cancellationToken);
        if (session is not null)
        {
            session.Status = status;
            session.StoppedAt = clock.UtcNow;
            session.LastError = status == LocalProxySessionStatus.Error ? reason : session.LastError;
            session.LastActivityAt = running.LastActivityAt;
            session.LastRequestAt = running.LastRequestAt;
            session.AccountId = running.AccountId;
            session.CodespaceName = running.CodespaceName;
            session.RemoteProxyPort = running.RemoteProxyPort;
            session.TotalRequests = running.TotalRequests;
            session.TotalConnectTunnels = 0;
            session.TotalBytesReceived = 0;
            session.TotalBytesSent = 0;
        }

        if (profile is not null)
        {
            profile.Status = status == LocalProxySessionStatus.Error ? LocalProxyProfileStatus.Error : LocalProxyProfileStatus.Stopped;
            profile.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetLastErrorAsync(RunningLocalProxy running, string? error, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.LastError = error;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordPublicRequestAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        running.LastRequestAt = now;
        running.LastActivityAt = now;
        running.LastGatewayActivityPersistedAt = now;
        running.TotalRequests++;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.LastRequestAt = now;
        session.LastActivityAt = now;
        session.TotalRequests = running.TotalRequests;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordPublicActivityAsync(RunningLocalProxy running, DateTimeOffset activityAt, CancellationToken cancellationToken)
    {
        running.LastActivityAt = activityAt;
        running.LastGatewayActivityPersistedAt = activityAt;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.LastActivityAt = activityAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<LocalProxyRuntimeState> ToResponseAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.Include(x => x.Profile).FirstAsync(x => x.Id == running.SessionId, cancellationToken);
        return ToRuntimeState(
            session,
            session.Profile!,
            running.MixedListener?.ActiveConnections ?? 0,
            running.AccountId,
            running.CodespaceName,
            running.RemoteProxyPort,
            running.LocalTunnelPort);
    }

    private static LocalProxyRuntimeState ToRuntimeState(
        LocalProxySession session,
        LocalProxyProfile profile,
        int activeConnections,
        Guid? accountId = null,
        string? codespaceName = null,
        int? remoteProxyPort = null,
        int? localTunnelPort = null)
    {
        var idleAt = session.LastActivityAt.AddMinutes(Math.Max(1, profile.IdleShutdownMinutes));
        var httpProxyUrl = $"http://127.0.0.1:{session.LocalPort}";
        var socksProxyUrl = $"socks5h://127.0.0.1:{session.SocksPort}";
        return new LocalProxyRuntimeState(
            session.Id,
            profile.Id,
            profile.Name,
            session.Status,
            session.BindHost,
            session.LocalPort,
            session.SocksPort,
            httpProxyUrl,
            httpProxyUrl,
            socksProxyUrl,
            session.StartedAt,
            session.LastActivityAt,
            session.LastRequestAt,
            idleAt,
            session.StoppedAt,
            session.LastError,
            session.TotalRequests,
            session.TotalConnectTunnels,
            session.TotalBytesReceived,
            session.TotalBytesSent,
            activeConnections,
            accountId ?? session.AccountId,
            codespaceName ?? session.CodespaceName,
            remoteProxyPort ?? session.RemoteProxyPort,
            localTunnelPort);
    }

    private string GetRuntimeDirectory(Guid sessionId)
    {
        var root = string.IsNullOrWhiteSpace(_options.XrayConfigDirectory)
            ? Path.Combine(environment.ContentRootPath, "data", "xray")
            : _options.XrayConfigDirectory;
        return Path.Combine(root, sessionId.ToString("N"));
    }

    private async Task StopCodespaceAfterStartFailureAsync(CodespaceProxyLaunch launch, Guid sessionId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var codespaces = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
            await codespaces.StopAsync(launch.AccountId, launch.CodespaceName, CancellationToken.None);
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.start_failure_stopped",
                OperationalEventSeverity.Information,
                "Stopped Codespace after local proxy startup failed.",
                NodeId: launch.AccountId,
                SessionId: sessionId,
                Details: new { launch.Username, launch.CodespaceName }),
                CancellationToken.None);
        }
        catch (Exception stopEx)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.start_failure_stop_failed",
                OperationalEventSeverity.Warning,
                "Could not stop Codespace after local proxy startup failed.",
                NodeId: launch.AccountId,
                SessionId: sessionId,
                StandardError: stopEx.Message,
                Details: new { launch.Username, launch.CodespaceName }),
                CancellationToken.None);
        }
    }

    private async Task StopBackingCodespaceAsync(RunningLocalProxy running, string reason, CancellationToken cancellationToken)
    {
        if (running.AccountId is null || string.IsNullOrWhiteSpace(running.CodespaceName))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var codespaces = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
            await codespaces.StopAsync(running.AccountId.Value, running.CodespaceName, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.backing_codespace.stopped",
                OperationalEventSeverity.Information,
                "Stopped backing Codespace for the local proxy.",
                NodeId: running.AccountId,
                SessionId: running.SessionId,
                Details: new { running.CodespaceName, Reason = reason }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "codespace_proxy.backing_codespace.stop_failed",
                OperationalEventSeverity.Warning,
                "Could not stop backing Codespace for the local proxy.",
                NodeId: running.AccountId,
                SessionId: running.SessionId,
                StandardError: ex.Message,
                Details: new { running.CodespaceName, Reason = reason }),
                CancellationToken.None);
        }
    }

    private static bool IsCodespaceRunning(string state) =>
        state.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

    private static bool IsStartingAutomationPhase(string phase) =>
        phase.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("StartingCodespace", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("SelectingAccount", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("Selected", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("EnsuringRemoteProxy", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("OpeningTunnel", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("TunnelReady", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdleStopReason(string reason) =>
        reason.Contains("idle", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdleStoppedSession(LocalProxySession session, LocalProxyProfile profile) =>
        session.Status == LocalProxySessionStatus.Stopped &&
        session.StoppedAt is not null &&
        session.StoppedAt.Value >= session.LastActivityAt.AddMinutes(Math.Max(1, profile.IdleShutdownMinutes)).AddSeconds(-10);

    private static string? TryGetUnavailablePortMessage(string bindHost, int port)
    {
        var bindAddress = ResolveBindAddress(bindHost);
        return IsPortAvailable(bindAddress, port) ? null : $"Proxy port {port} is unavailable.";
    }

    private static bool IsPortAvailable(IPAddress bindAddress, int port)
    {
        try
        {
            using var listener = new TcpListener(bindAddress, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress ResolveBindAddress(string bindHost)
    {
        if (string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(bindHost, out var address) ? address : IPAddress.Loopback;
    }

    private sealed class RunningLocalProxy(
        Guid profileId,
        string profileName,
        Guid sessionId,
        string bindHost,
        int httpPort,
        int socksPort,
        int internalHttpPort,
        int internalSocksPort,
        string? proxyUsername,
        string? proxyPassword,
        int idleShutdownMinutes,
        XrayProcessHandle process,
        MixedProxyListener? mixedListener,
        Process? tunnelProcess,
        SshDirectTcpBridge? sshDirectBridge,
        string configPath,
        string accessLogPath,
        string errorLogPath,
        Guid? accountId,
        string? accountUsername,
        string? codespaceName,
        int? remoteProxyPort,
        int? localTunnelPort)
    {
        private bool _stopped;

        public Guid ProfileId { get; } = profileId;
        public string ProfileName { get; } = profileName;
        public Guid SessionId { get; } = sessionId;
        public string BindHost { get; } = bindHost;
        public int HttpPort { get; } = httpPort;
        public int SocksPort { get; } = socksPort;
        public int InternalHttpPort { get; } = internalHttpPort;
        public int InternalSocksPort { get; } = internalSocksPort;
        public int ProbeHttpPort => MixedListener is null ? InternalHttpPort : HttpPort;
        public int ProbeSocksPort => MixedListener is null ? InternalSocksPort : SocksPort;
        public string? ProxyUsername { get; } = proxyUsername;
        public string? ProxyPassword { get; } = proxyPassword;
        public int IdleShutdownMinutes { get; } = idleShutdownMinutes;
        public XrayProcessHandle Process { get; } = process;
        public MixedProxyListener? MixedListener { get; private set; } = mixedListener;
        public Process? TunnelProcess { get; } = tunnelProcess;
        public SshDirectTcpBridge? SshDirectBridge { get; } = sshDirectBridge;
        public string ConfigPath { get; } = configPath;
        public string AccessLogPath { get; } = accessLogPath;
        public string ErrorLogPath { get; } = errorLogPath;
        public Guid? AccountId { get; } = accountId;
        public string? AccountUsername { get; } = accountUsername;
        public string? CodespaceName { get; } = codespaceName;
        public int? RemoteProxyPort { get; } = remoteProxyPort;
        public int? LocalTunnelPort { get; } = localTunnelPort;
        public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ProxyUsername) && !string.IsNullOrWhiteSpace(ProxyPassword);
        public bool IsStopped => _stopped;
        public DateTimeOffset LastActivityAt;
        public DateTimeOffset? LastRequestAt;
        public DateTimeOffset LastGatewayActivityPersistedAt = DateTimeOffset.MinValue;
        public long TotalRequests;

        public void AttachMixedListener(MixedProxyListener listener)
        {
            MixedListener = listener;
        }

        public void MarkStopped()
        {
            _stopped = true;
        }
    }

    private sealed record IdleWakePause(
        Guid ProfileId,
        Guid? AccountId,
        string? AccountUsername,
        string? CodespaceName,
        int RequestCount,
        DateTimeOffset WindowStartedAt,
        DateTimeOffset WindowExpiresAt);

    private sealed record IdleWakePolicy(int RequestThreshold, TimeSpan Window);

    private sealed record IdleWakeDecision(
        bool ShouldStart,
        int RequestCount,
        int RequestThreshold,
        DateTimeOffset WindowExpiresAt,
        string Message);

    private sealed record CodespaceTunnelHandle(Process? Process, SshDirectTcpBridge? SshDirectBridge);
}

public sealed record LocalProxyRuntimeState(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    LocalProxySessionStatus Status,
    string BindHost,
    int LocalPort,
    int SocksPort,
    string ProxyUrl,
    string HttpProxyUrl,
    string SocksProxyUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? LastRequestAt,
    DateTimeOffset IdleShutdownAt,
    DateTimeOffset? StoppedAt,
    string? LastError,
    long TotalRequests,
    long TotalConnectTunnels,
    long TotalBytesReceived,
    long TotalBytesSent,
    int ActiveConnections,
    Guid? AccountId,
    string? CodespaceName,
    int? RemoteProxyPort,
    int? LocalTunnelPort);

public sealed record LocalProxyRuntimeResult(bool Succeeded, string Message, LocalProxyRuntimeState? Session);

public sealed record LocalProxyGatewayTarget(
    int InternalHttpPort,
    int InternalSocksPort,
    Guid SessionId,
    Guid? AccountId,
    string? CodespaceName);

public sealed class LocalProxyIdleWakePendingException(string message) : InvalidOperationException(message);

public sealed record LocalProxyAutomationRuntimeStatus(
    string Phase,
    Guid? AccountId,
    string? AccountUsername,
    string? CodespaceName,
    string? Warning,
    DateTimeOffset? NextRetryAt,
    string? LastError,
    DateTimeOffset? LastRequestAt = null,
    bool IdleWakePaused = false,
    int IdleWakeRequestCount = 0,
    int IdleWakeRequestThreshold = 0,
    DateTimeOffset? IdleWakeWindowExpiresAt = null);

public sealed record LocalProxyStartResult(bool Succeeded, string Message, LocalProxyRuntimeState? Session)
{
    public static LocalProxyStartResult Ok(string message, LocalProxyRuntimeState? session) => new(true, message, session);
    public static LocalProxyStartResult Fail(string message, LocalProxyRuntimeState? session) => new(false, message, session);
}

public sealed record CodespaceProxyLaunch(
    Guid AccountId,
    string Username,
    string Token,
    string CodespaceName,
    int RemoteProxyPort,
    bool StopCodespaceOnStartFailure);

public sealed record CodespaceRetryPlan(
    Guid AccountId,
    Guid ProfileId,
    string? Username,
    string CodespaceName,
    int Attempt,
    DateTimeOffset NextRetryAt,
    string LastError);

public sealed record SavedCodespaceProxySession(
    Guid ProfileId,
    Guid AccountId,
    string? AccountUsername,
    string CodespaceName,
    string? LastError);
