using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class LocalProxyEndpoints
{
    internal const int LatestGatewayRequestLimit = 20;

    public static IEndpointRouteBuilder MapLocalProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/local-proxy");

        group.MapGet("/profiles", async (AppDbContext db, CancellationToken ct) =>
        {
            var profile = await GetOrCreateSettingsProfileAsync(db, null, ct);
            return new[] { ToResponse(profile) };
        });

        group.MapGet("/settings", async (AppDbContext db, IClock clock, CancellationToken ct) =>
            Results.Ok(ToResponse(await GetOrCreateSettingsProfileAsync(db, clock, ct))));

        group.MapPut("/settings", async (LocalProxySettingsRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var profile = await GetOrCreateSettingsProfileAsync(db, clock, ct);
            var validation = ValidateSettings(request, requirePasswordForNewAuth: !string.IsNullOrWhiteSpace(request.ProxyUsername) && string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword));
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            ApplySettings(profile, request, secrets, clock);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.settings.update", "Updated Codespace proxy settings.", null, ct);
            return Results.Ok(ToResponse(profile));
        });

        group.MapGet("/status", async (AppDbContext db, LocalProxyRuntimeService runtime, IClock clock, CancellationToken ct) =>
        {
            var settings = await GetOrCreateSettingsProfileAsync(db, clock, ct);
            var activeSession = await runtime.GetActiveAsync(ct);
            var runtimeStatus = runtime.GetAutomationStatus();
            var latestSession = activeSession is null
                ? (await db.LocalProxySessions
                    .Include(x => x.Profile)
                    .Where(x => x.ProfileId == settings.Id)
                    .ToListAsync(ct))
                    .OrderByDescending(x => x.StartedAt)
                    .FirstOrDefault()
                : null;
            var selectedAccount = runtimeStatus.AccountUsername;
            var selectedAccountId = activeSession?.AccountId ?? latestSession?.AccountId;
            if (selectedAccount is null && selectedAccountId is not null)
            {
                selectedAccount = await db.GitHubAccounts
                    .AsNoTracking()
                    .Where(x => x.Id == selectedAccountId.Value)
                    .Select(x => x.Username)
                    .FirstOrDefaultAsync(ct);
            }

            var phase = activeSession?.Status.ToString() ?? runtimeStatus.Phase;
            var warning = runtimeStatus.Warning;
            var lastError = runtimeStatus.LastError ?? latestSession?.LastError;
            if (activeSession is null && latestSession is not null)
            {
                if (IsIdleStopped(latestSession, settings))
                {
                    phase = "ZzzIdle";
                    warning ??= $"Idle timeout reached after {settings.IdleShutdownMinutes} minutes. The backing Codespace was stopped to save usage.";
                }
                else if (latestSession.Status == LocalProxySessionStatus.Error &&
                         phase == "WaitingForTraffic" &&
                         !string.Equals(latestSession.LastError, DatabaseSchemaInitializer.RestartedActiveSessionMessage, StringComparison.Ordinal))
                {
                    phase = "Error";
                }
                else if (latestSession.Status == LocalProxySessionStatus.Starting && phase == "WaitingForTraffic")
                {
                    phase = "Starting";
                }
            }

            var sessionResponse = activeSession is not null
                ? ToResponse(activeSession)
                : latestSession is null
                    ? null
                    : ToResponse(latestSession, latestSession.Profile ?? settings, runtimeStatus.AccountId ?? latestSession.AccountId, runtimeStatus.CodespaceName ?? latestSession.CodespaceName, 0);
            var statusSummary = BuildStatusSummary(phase, activeSession, runtimeStatus, settings, lastError);
            int? retryInSeconds = runtimeStatus.NextRetryAt is null
                ? null
                : Math.Max(0, (int)Math.Ceiling((runtimeStatus.NextRetryAt.Value - clock.UtcNow).TotalSeconds));
            var lastRequestAt = GetLatestRequestAt(runtimeStatus.LastRequestAt, activeSession?.LastRequestAt, latestSession?.LastRequestAt);
            var latestRequests = ToLatestRequestResponses(await db.LocalProxyGatewayRequests.AsNoTracking().ToListAsync(ct));

            return Results.Ok(new LocalProxyAutomationStatusResponse(
                ToResponse(settings),
                sessionResponse,
                phase,
                selectedAccount,
                runtimeStatus.CodespaceName ?? activeSession?.CodespaceName ?? latestSession?.CodespaceName,
                warning,
                runtimeStatus.NextRetryAt,
                lastError,
                statusSummary.Availability,
                statusSummary.Message,
                statusSummary.Severity,
                statusSummary.PublicPortOpen,
                retryInSeconds,
                lastRequestAt,
                runtimeStatus.IdleWakePaused,
                runtimeStatus.IdleWakeRequestCount,
                runtimeStatus.IdleWakeRequestThreshold,
                runtimeStatus.IdleWakeWindowExpiresAt,
                latestRequests));
        });

        group.MapPost("/profiles", async (LocalProxyProfileRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            if (await db.LocalProxyProfiles.AnyAsync(ct))
            {
                return Results.Conflict(new { error = "Only one Codespace proxy settings profile is supported. Update /api/local-proxy/settings instead." });
            }

            var socksPort = request.SocksPort ?? request.LocalPort;
            var validation = await ValidateAsync(request, socksPort, db, null, requirePasswordForNewAuth: true, ct);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var name = request.Name.Trim();
            if (await db.LocalProxyProfiles.AnyAsync(x => x.Name == name, ct))
            {
                return Results.Conflict(new { error = "A local proxy profile with this name already exists." });
            }

            var now = clock.UtcNow;
            var profile = new LocalProxyProfile
            {
                Name = name,
                BindHost = request.BindHost.Trim(),
                LocalPort = request.LocalPort,
                SocksPort = socksPort,
                ProxyUsername = NullIfEmpty(request.ProxyUsername),
                ProtectedProxyPassword = string.IsNullOrWhiteSpace(request.ProxyPassword) ? null : secrets.Protect(request.ProxyPassword.Trim()),
                IdleShutdownMinutes = request.IdleShutdownMinutes,
                Notes = NullIfEmpty(request.Notes),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.LocalProxyProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.create", "Created local proxy profile.", null, ct);
            return Results.Created($"/api/local-proxy/profiles/{profile.Id}", ToResponse(profile));
        });

        group.MapPut("/profiles/{id:guid}", async (Guid id, LocalProxyProfileRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var profile = await db.LocalProxyProfiles.FindAsync([id], ct);
            if (profile is null)
            {
                return Results.NotFound();
            }

            var wantsAuth = !string.IsNullOrWhiteSpace(request.ProxyUsername);
            var hasExistingPassword = !string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword);
            var socksPort = request.SocksPort ?? request.LocalPort;
            var validation = await ValidateAsync(request, socksPort, db, id, requirePasswordForNewAuth: wantsAuth && !hasExistingPassword, ct);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var name = request.Name.Trim();
            if (await db.LocalProxyProfiles.AnyAsync(x => x.Id != id && x.Name == name, ct))
            {
                return Results.Conflict(new { error = "A local proxy profile with this name already exists." });
            }

            profile.Name = name;
            profile.BindHost = request.BindHost.Trim();
            profile.LocalPort = request.LocalPort;
            profile.SocksPort = socksPort;
            profile.ProxyUsername = NullIfEmpty(request.ProxyUsername);
            if (string.IsNullOrWhiteSpace(request.ProxyUsername))
            {
                profile.ProtectedProxyPassword = null;
            }
            else if (!string.IsNullOrWhiteSpace(request.ProxyPassword))
            {
                profile.ProtectedProxyPassword = secrets.Protect(request.ProxyPassword.Trim());
            }

            profile.IdleShutdownMinutes = request.IdleShutdownMinutes;
            profile.Notes = NullIfEmpty(request.Notes);
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.update", "Updated local proxy profile.", null, ct);
            return Results.Ok(ToResponse(profile));
        });

        group.MapDelete("/profiles/{id:guid}", async (Guid id, AppDbContext db, LocalProxyRuntimeService runtime, AuditService audit, CancellationToken ct) =>
        {
            var active = await runtime.GetActiveAsync(ct);
            if (active?.ProfileId == id)
            {
                return Results.Conflict(new { error = "Stop the active local proxy session before deleting this profile." });
            }

            var profile = await db.LocalProxyProfiles.FindAsync([id], ct);
            if (profile is null)
            {
                return Results.NotFound();
            }

            db.LocalProxyProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.delete", "Deleted local proxy profile.", null, ct);
            return Results.NoContent();
        });

        group.MapGet("/session", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var session = ToResponse(await runtime.GetActiveAsync(ct));
            return session is null ? Results.Text("null", "application/json") : Results.Ok(session);
        });

        group.MapGet("/statistics", async (string? period, LocalProxyStatisticsService statistics, CancellationToken ct) =>
            Results.Ok(await statistics.GetAsync(period, ct)));

        group.MapPost("/profiles/{id:guid}/start", async (Guid id, LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.StartAsync(id, ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        group.MapPost("/stop", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.StopAsync("Stopped by user.", ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        group.MapPost("/probe", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.ProbeActiveAsync(ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        group.MapPost("/retry", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.RetryCodespaceProxyAsync(ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        return app;
    }

    private static LocalProxyProfileResponse ToResponse(LocalProxyProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.BindHost,
            profile.LocalPort,
            profile.SocksPort,
            profile.ProxyUsername,
            !string.IsNullOrWhiteSpace(profile.ProxyUsername) && !string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword),
            profile.IdleShutdownMinutes,
            profile.Notes,
            profile.Status,
            profile.CreatedAt,
            profile.UpdatedAt);

    public static LocalProxySessionResponse? ToResponse(LocalProxyRuntimeState? state) =>
        state is null
            ? null
            : new LocalProxySessionResponse(
                state.Id,
                state.ProfileId,
                state.ProfileName,
                state.Status,
                state.BindHost,
                state.LocalPort,
                state.SocksPort,
                state.ProxyUrl,
                state.HttpProxyUrl,
                state.SocksProxyUrl,
                state.StartedAt,
                state.LastActivityAt,
                state.LastRequestAt,
                state.IdleShutdownAt,
                state.StoppedAt,
                state.LastError,
                state.TotalRequests,
                state.TotalConnectTunnels,
                state.TotalBytesReceived,
                state.TotalBytesSent,
                state.ActiveConnections,
                state.AccountId,
                state.CodespaceName,
                state.RemoteProxyPort,
                state.LocalTunnelPort);

    private static LocalProxySessionResponse ToResponse(
        LocalProxySession session,
        LocalProxyProfile profile,
        Guid? accountId,
        string? codespaceName,
        int activeConnections)
    {
        var idleAt = session.LastActivityAt.AddMinutes(Math.Max(1, profile.IdleShutdownMinutes));
        var httpProxyUrl = $"http://127.0.0.1:{session.LocalPort}";
        var socksProxyUrl = $"socks5h://127.0.0.1:{session.SocksPort}";
        return new LocalProxySessionResponse(
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
            accountId,
            codespaceName,
            null,
            null);
    }

    internal static DateTimeOffset? GetLatestRequestAt(params DateTimeOffset?[] timestamps)
    {
        DateTimeOffset? latest = null;
        foreach (var timestamp in timestamps)
        {
            if (timestamp is not null && (latest is null || timestamp > latest))
            {
                latest = timestamp;
            }
        }

        return latest;
    }

    internal static IReadOnlyList<LocalProxyGatewayRequestResponse> ToLatestRequestResponses(
        IEnumerable<LocalProxyGatewayRequest> requests,
        int limit = LatestGatewayRequestLimit) =>
        requests
            .OrderByDescending(x => x.ObservedAt)
            .Take(Math.Max(0, limit))
            .Select(x => new LocalProxyGatewayRequestResponse(
                x.Id,
                x.ObservedAt,
                x.Protocol,
                x.TargetHost,
                x.TargetPort,
                x.Outcome,
                x.SessionId,
                x.AccountId,
                x.CodespaceName,
                x.ErrorMessage,
                x.DurationMs))
            .ToList();

    private static LocalProxyStatusSummary BuildStatusSummary(
        string phase,
        LocalProxyRuntimeState? activeSession,
        LocalProxyAutomationRuntimeStatus runtimeStatus,
        LocalProxyProfile settings,
        string? lastError)
    {
        var normalizedPhase = phase.Trim();
        if (activeSession?.Status == LocalProxySessionStatus.Running && string.Equals(normalizedPhase, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalProxyStatusSummary(
                "Up",
                $"Proxy is up on {settings.BindHost}:{settings.LocalPort}.",
                "success",
                PublicPortOpen: true);
        }

        if (string.Equals(normalizedPhase, "Retrying", StringComparison.OrdinalIgnoreCase))
        {
            var retryText = "Retry is scheduled. The wake gateway is listening and incoming traffic can retry startup immediately.";
            return new LocalProxyStatusSummary(
                "Retrying",
                string.IsNullOrWhiteSpace(lastError) ? retryText : $"{lastError} {retryText}",
                "warning",
                PublicPortOpen: true);
        }

        if (IsStartingPhase(normalizedPhase))
        {
            return new LocalProxyStatusSummary(
                "Starting",
                $"Wake gateway is accepting traffic. Proxy backend is {normalizedPhase}; requests attach after readiness probes pass.",
                "info",
                PublicPortOpen: true);
        }

        if (string.Equals(normalizedPhase, "ZzzIdle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPhase, "Stopped", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPhase, "WaitingForTraffic", StringComparison.OrdinalIgnoreCase))
        {
            if (runtimeStatus.IdleWakePaused && runtimeStatus.IdleWakeRequestThreshold > 1)
            {
                var remaining = Math.Max(0, runtimeStatus.IdleWakeRequestThreshold - runtimeStatus.IdleWakeRequestCount);
                return new LocalProxyStatusSummary(
                    "Idle",
                    $"Proxy is idle-paused. {remaining} more proxy request(s) within the wake window will start the Codespace.",
                    "muted",
                    PublicPortOpen: true);
            }

            return new LocalProxyStatusSummary(
                "Idle",
                "Proxy is idle. The wake gateway is listening and incoming proxy traffic will start the Codespace.",
                "muted",
                PublicPortOpen: true);
        }

        return new LocalProxyStatusSummary(
            "Down",
            string.IsNullOrWhiteSpace(lastError)
                ? "Proxy backend is down. The wake gateway may return 503 until startup succeeds."
                : $"{lastError} The wake gateway may return 503 until startup succeeds.",
            "error",
            PublicPortOpen: true);
    }

    private static bool IsStartingPhase(string phase) =>
        phase.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("StartingCodespace", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("SelectingAccount", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("Selected", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("EnsuringRemoteProxy", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("OpeningTunnel", StringComparison.OrdinalIgnoreCase) ||
        phase.Equals("TunnelReady", StringComparison.OrdinalIgnoreCase);

    private sealed record LocalProxyStatusSummary(
        string Availability,
        string Message,
        string Severity,
        bool PublicPortOpen);

    private static bool IsIdleStopped(LocalProxySession session, LocalProxyProfile profile) =>
        session.Status == LocalProxySessionStatus.Stopped &&
        session.StoppedAt is not null &&
        session.StoppedAt.Value >= session.LastActivityAt.AddMinutes(Math.Max(1, profile.IdleShutdownMinutes)).AddSeconds(-10);

    private static async Task<LocalProxyProfile> GetOrCreateSettingsProfileAsync(AppDbContext db, IClock? clock, CancellationToken ct)
    {
        var profiles = await db.LocalProxyProfiles
            .ToListAsync(ct);
        var profile = profiles
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefault();
        if (profile is not null)
        {
            return profile;
        }

        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        profile = new LocalProxyProfile
        {
            Name = "Default",
            BindHost = "127.0.0.1",
            LocalPort = 8910,
            SocksPort = 8910,
            IdleShutdownMinutes = 30,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.LocalProxyProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    private static object? ValidateSettings(LocalProxySettingsRequest request, bool requirePasswordForNewAuth)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.BindHost), request.BindHost);
        AddPort(errors, nameof(request.LocalPort), request.LocalPort);

        if (request.IdleShutdownMinutes is < 1 or > 1440)
        {
            errors[nameof(request.IdleShutdownMinutes)] = ["Idle shutdown must be between 1 and 1440 minutes."];
        }

        var hasUsername = !string.IsNullOrWhiteSpace(request.ProxyUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(request.ProxyPassword);
        if (hasPassword && !hasUsername)
        {
            errors[nameof(request.ProxyUsername)] = ["Username is required when password is set."];
        }

        if (requirePasswordForNewAuth && hasUsername && !hasPassword)
        {
            errors[nameof(request.ProxyPassword)] = ["Password is required when enabling proxy authentication."];
        }

        return errors.Count == 0 ? null : new { errors };
    }

    private static void ApplySettings(LocalProxyProfile profile, LocalProxySettingsRequest request, ISecretProtector secrets, IClock clock)
    {
        profile.Name = "Default";
        profile.BindHost = request.BindHost.Trim();
        profile.LocalPort = request.LocalPort;
        profile.SocksPort = request.LocalPort;
        profile.ProxyUsername = NullIfEmpty(request.ProxyUsername);
        if (string.IsNullOrWhiteSpace(request.ProxyUsername))
        {
            profile.ProtectedProxyPassword = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.ProxyPassword))
        {
            profile.ProtectedProxyPassword = secrets.Protect(request.ProxyPassword.Trim());
        }

        profile.IdleShutdownMinutes = request.IdleShutdownMinutes;
        profile.Notes = null;
        profile.UpdatedAt = clock.UtcNow;
    }

    private static async Task<object?> ValidateAsync(LocalProxyProfileRequest request, int socksPort, AppDbContext db, Guid? existingProfileId, bool requirePasswordForNewAuth, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.Name), request.Name);
        AddRequired(errors, nameof(request.BindHost), request.BindHost);
        AddPort(errors, nameof(request.LocalPort), request.LocalPort);
        AddPort(errors, nameof(request.SocksPort), socksPort);

        var portOwner = await db.LocalProxyProfiles
            .AsNoTracking()
            .Where(x => existingProfileId == null || x.Id != existingProfileId)
            .Where(x => x.LocalPort == request.LocalPort ||
                        x.SocksPort == request.LocalPort)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);
        if (portOwner is not null)
        {
            errors[nameof(request.LocalPort)] = [$"Proxy port overlaps with profile \"{portOwner}\"."];
        }

        if (request.IdleShutdownMinutes is < 1 or > 1440)
        {
            errors[nameof(request.IdleShutdownMinutes)] = ["Idle shutdown must be between 1 and 1440 minutes."];
        }

        var hasUsername = !string.IsNullOrWhiteSpace(request.ProxyUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(request.ProxyPassword);
        if (hasPassword && !hasUsername)
        {
            errors[nameof(request.ProxyUsername)] = ["Username is required when password is set."];
        }

        if (requirePasswordForNewAuth && hasUsername && !hasPassword)
        {
            errors[nameof(request.ProxyPassword)] = ["Password is required when enabling proxy authentication."];
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

    private static void AddPort(Dictionary<string, string[]> errors, string field, int value)
    {
        if (value is < 1 or > 65535)
        {
            errors[field] = ["Port must be between 1 and 65535."];
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
