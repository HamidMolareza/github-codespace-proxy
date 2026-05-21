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
    private RunningLocalProxy? _running;

    public Task<LocalProxyStartResult> StartAsync(Guid profileId, CancellationToken cancellationToken) =>
        StartInternalAsync(profileId, null, cancellationToken);

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

        var codespaces = scope.ServiceProvider.GetRequiredService<GitHubCodespaceService>();
        var beforeStart = await codespaces.RefreshCodespaceAsync(accountId, codespaceName, cancellationToken);
        var stopOnStartFailure = beforeStart is null || !IsCodespaceRunning(beforeStart.State);
        await codespaces.StartAsync(accountId, codespaceName, cancellationToken);

        return await StartInternalAsync(
            selectedProfileId,
            new CodespaceProxyLaunch(accountId, account.Username, token, codespaceName, _options.CodespaceRemoteProxyPort, stopOnStartFailure),
            cancellationToken);
    }

    private async Task<LocalProxyStartResult> StartInternalAsync(Guid profileId, CodespaceProxyLaunch? codespace, CancellationToken cancellationToken)
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
            var portError = TryGetUnavailablePortMessage(listenerBindHost, publicPort);

            var session = new LocalProxySession
            {
                ProfileId = profile.Id,
                Status = LocalProxySessionStatus.Starting,
                BindHost = profile.BindHost,
                LocalPort = publicPort,
                SocksPort = publicPort,
                StartedAt = clock.UtcNow,
                LastActivityAt = clock.UtcNow
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
                    : $"Starting Codespace-backed Xray proxy on {profile.BindHost}:{publicPort}.",
                Details: new { profile.Id, profile.BindHost, ListenerBindHost = listenerBindHost, PublicPort = publicPort, codespace?.CodespaceName, codespace?.RemoteProxyPort }),
                cancellationToken);

            if (portError is not null)
            {
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = portError;
                profile.Status = LocalProxyProfileStatus.Error;
                await db.SaveChangesAsync(cancellationToken);
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
            MixedProxyListener? mixedListener = null;
            XrayProcessHandle? handle = null;

            try
            {
                if (codespace is not null && tunnelPort is not null)
                {
                    tunnelProcess = await StartCodespaceTunnelAsync(codespace, session.Id, tunnelPort.Value, cancellationToken);
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

                mixedListener = MixedProxyListener.Start(listenerBindHost, publicPort, internalHttpPort, internalSocksPort, logger);
                if (!await WaitForPublicPortAsync(publicPort, TimeSpan.FromSeconds(4), cancellationToken))
                {
                    throw new InvalidOperationException("Mixed HTTP/SOCKS listener did not open the public port in time.");
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
                    configPath,
                    accessLogPath,
                    errorLogPath,
                    codespace?.AccountId,
                    codespace?.CodespaceName,
                    codespace?.RemoteProxyPort,
                    tunnelPort);
                state.LastActivityAt = session.LastActivityAt;
                _running = state;
                _ = Task.Run(() => MonitorProcessAsync(state), CancellationToken.None);
                if (tunnelProcess is not null)
                {
                    _ = Task.Run(() => MonitorTunnelAsync(state), CancellationToken.None);
                }

                session.Status = LocalProxySessionStatus.Running;
                profile.Status = LocalProxyProfileStatus.Running;
                profile.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.xray.started",
                    OperationalEventSeverity.Information,
                    codespace is null
                        ? $"One-port Xray proxy is listening on {profile.BindHost}:{publicPort}."
                        : $"Codespace proxy is ready on {profile.BindHost}:{publicPort}.",
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, handle.ProcessId, PublicPort = publicPort, internalHttpPort, internalSocksPort, tunnelPort, codespace?.CodespaceName }),
                    cancellationToken);

                var probe = await ProbeActiveAsync(cancellationToken);
                return probe.Succeeded
                    ? LocalProxyStartResult.Ok("Codespace-backed proxy is ready.", probe.Session)
                    : LocalProxyStartResult.Ok($"Proxy is listening, but probe failed: {probe.Message}", probe.Session);
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

                StopProcess(tunnelProcess);
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = ex.Message;
                profile.Status = LocalProxyProfileStatus.Error;
                await db.SaveChangesAsync(cancellationToken);
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
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.stop.none",
                    OperationalEventSeverity.Warning,
                    "Stop requested but no local Xray proxy session was running."),
                    cancellationToken);
                return new LocalProxyRuntimeResult(true, "No local proxy session is running.", null);
            }

            await RefreshRuntimeStatsAsync(running, cancellationToken);
            running.MarkStopped();
            await running.MixedListener.DisposeAsync();
            await running.Process.StopAsync();
            StopProcess(running.TunnelProcess);
            await PersistStoppedAsync(running, reason, LocalProxySessionStatus.Stopped, cancellationToken);
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

        var errors = new List<string>();
        try
        {
            await ProbeHttpAsync(running, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.probe.http.success",
                OperationalEventSeverity.Information,
                "Local Xray HTTP proxy probe succeeded.",
                Details: new { running.ProfileId, running.SessionId, running.HttpPort }),
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
                Details: new { running.ProfileId, running.SessionId, running.HttpPort }),
                cancellationToken);
        }

        try
        {
            await ProbeSocksAsync(running, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.probe.socks.success",
                OperationalEventSeverity.Information,
                "Local Xray SOCKS proxy probe succeeded.",
                Details: new { running.ProfileId, running.SessionId, running.SocksPort }),
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
                Details: new { running.ProfileId, running.SessionId, running.SocksPort }),
                cancellationToken);
        }

        await RefreshRuntimeStatsAsync(running, cancellationToken);
        if (errors.Count == 0)
        {
            await SetLastErrorAsync(running, null, cancellationToken);
            return new LocalProxyRuntimeResult(true, "Local Xray HTTP and SOCKS probes succeeded.", await ToResponseAsync(running, cancellationToken));
        }

        var message = string.Join(" ", errors);
        await SetLastErrorAsync(running, message, cancellationToken);
        return new LocalProxyRuntimeResult(false, $"Local Xray proxy probe failed: {message}", await ToResponseAsync(running, cancellationToken));
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
        var idleFor = clock.UtcNow - running.LastActivityAt;
        if (idleFor < TimeSpan.FromMinutes(running.IdleShutdownMinutes))
        {
            return;
        }

        await StopAsync($"Stopped after {idleFor.TotalMinutes:n1} idle minutes.", cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "local_proxy.idle.timeout",
            OperationalEventSeverity.Information,
            "Stopped local Xray proxy after idle timeout.",
            Details: new { running.ProfileId, running.SessionId, IdleMinutes = idleFor.TotalMinutes, running.IdleShutdownMinutes }),
            cancellationToken);
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
            await running.MixedListener.DisposeAsync();
            StopProcess(running.TunnelProcess);
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
            await running.MixedListener.DisposeAsync();
            await running.Process.StopAsync();
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

    private async Task ProbeHttpAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{running.HttpPort}"),
            UseProxy = true
        };
        if (running.RequiresAuthentication)
        {
            handler.Proxy.Credentials = new NetworkCredential(running.ProxyUsername, running.ProxyPassword);
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ProbeUrl);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ProbeSocksAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        var uri = new Uri(_options.ProbeUrl);
        var port = uri.Port > 0 ? uri.Port : uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, running.SocksPort, cancellationToken);
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

    private async Task<Process> StartCodespaceTunnelAsync(CodespaceProxyLaunch launch, Guid sessionId, int localTunnelPort, CancellationToken cancellationToken)
    {
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.starting",
            OperationalEventSeverity.Information,
            "Waiting for Codespace SSH access.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { launch.Username, launch.CodespaceName, launch.RemoteProxyPort, LocalTunnelPort = localTunnelPort }),
            cancellationToken);

        await RunRequiredAsync(
            "codespace.ssh.ready",
            ["codespace", "ssh", "-c", launch.CodespaceName, "true"],
            launch,
            sessionId,
            TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceStartTimeoutSeconds, 30, 600)),
            cancellationToken);

        var sshConfigPath = await RefreshCodespacesSshConfigAsync(launch, sessionId, cancellationToken);

        await RunRequiredAsync(
            "codespace.proxy.remote_probe",
            ["codespace", "ssh", "-c", launch.CodespaceName, $"timeout 3 bash -lc '</dev/tcp/127.0.0.1/{launch.RemoteProxyPort}'"],
            launch,
            sessionId,
            TimeSpan.FromSeconds(20),
            cancellationToken);

        var sshHost = $"cs.{launch.CodespaceName}.main";
        var command = new CommandSpec(
            "ssh",
            [
                "-F", sshConfigPath,
                "-N",
                "-L", $"127.0.0.1:{localTunnelPort}:127.0.0.1:{launch.RemoteProxyPort}",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=10",
                "-o", "ServerAliveCountMax=3",
                "-o", "TCPKeepAlive=yes",
                sshHost
            ],
            Kind: "codespace.ssh.tunnel",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            EnvironmentVariables: DirectNetworkEnvironment.CreateGitHubCommandEnvironment(launch.Token));

        var process = commandRunner.Start(command);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceTunnelReadyTimeoutSeconds, 5, 120));
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"ssh exited before tunnel port {localTunnelPort} became ready.");
            }

            if (await CanConnectAsync(localTunnelPort, cancellationToken))
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "codespace_proxy.tunnel.ready",
                    OperationalEventSeverity.Information,
                    "Codespace SSH tunnel is ready.",
                    NodeId: launch.AccountId,
                    SessionId: sessionId,
                    Details: new { launch.CodespaceName, LocalTunnelPort = localTunnelPort, launch.RemoteProxyPort, process.Id }),
                    cancellationToken);
                return process;
            }

            await Task.Delay(250, cancellationToken);
        }

        StopProcess(process);
        throw new InvalidOperationException($"Timed out waiting for tunnel port {localTunnelPort}.");
    }

    private async Task<string> RefreshCodespacesSshConfigAsync(CodespaceProxyLaunch launch, Guid sessionId, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.CodespaceSshConfigTimeoutSeconds, 30, 600));
        var sshDirectory = GetCodespacesSshConfigDirectory();
        var target = Path.Combine(sshDirectory, $"{sessionId:N}.config");

        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.ssh_config.refreshing",
            OperationalEventSeverity.Information,
            "Refreshing Codespaces SSH config for the selected Codespace.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { launch.Username, launch.CodespaceName, Path = target, TimeoutSeconds = (int)timeout.TotalSeconds }),
            cancellationToken);

        var result = await RunRequiredAsync(
            "codespace.ssh.config",
            ["codespace", "ssh", "--config", "-c", launch.CodespaceName],
            launch,
            sessionId,
            timeout,
            cancellationToken);

        Directory.CreateDirectory(sshDirectory);
        var temp = Path.Combine(sshDirectory, $"{sessionId:N}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, result.StandardOutput, cancellationToken);
        File.Move(temp, target, overwrite: true);
        var lineCount = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        await events.WriteAsync(new OperationalEventWrite(
            "codespace_proxy.ssh_config.refreshed",
            OperationalEventSeverity.Information,
            "Refreshed Codespaces SSH config.",
            NodeId: launch.AccountId,
            SessionId: sessionId,
            Details: new { Path = target, Bytes = result.StandardOutput.Length, Lines = lineCount, TimeoutSeconds = (int)timeout.TotalSeconds }),
            cancellationToken);
        return target;
    }

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
            IdleShutdownMinutes = 30,
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

    private async Task RefreshRuntimeStatsAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        if (!File.Exists(running.AccessLogPath))
        {
            return;
        }

        var requestCount = File.ReadLines(running.AccessLogPath).LongCount();
        if (requestCount <= running.TotalRequests)
        {
            return;
        }

        running.TotalRequests = requestCount;
        running.LastActivityAt = File.GetLastWriteTimeUtc(running.AccessLogPath);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.LastActivityAt = running.LastActivityAt;
        session.TotalRequests = running.TotalRequests;
        await db.SaveChangesAsync(cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "local_proxy.activity.observed",
            OperationalEventSeverity.Debug,
            "Observed Xray access log activity.",
            Details: new { running.ProfileId, running.SessionId, running.TotalRequests }),
            cancellationToken);
    }

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

    private async Task<LocalProxyRuntimeState> ToResponseAsync(RunningLocalProxy running, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.Include(x => x.Profile).FirstAsync(x => x.Id == running.SessionId, cancellationToken);
        return ToRuntimeState(session, session.Profile!, running.MixedListener.ActiveConnections);
    }

    private static LocalProxyRuntimeState ToRuntimeState(LocalProxySession session, LocalProxyProfile profile, int activeConnections)
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
            idleAt,
            session.StoppedAt,
            session.LastError,
            session.TotalRequests,
            session.TotalConnectTunnels,
            session.TotalBytesReceived,
            session.TotalBytesSent,
            activeConnections);
    }

    private string GetRuntimeDirectory(Guid sessionId)
    {
        var root = string.IsNullOrWhiteSpace(_options.XrayConfigDirectory)
            ? Path.Combine(environment.ContentRootPath, "data", "xray")
            : _options.XrayConfigDirectory;
        return Path.Combine(root, sessionId.ToString("N"));
    }

    private string GetCodespacesSshConfigDirectory() =>
        string.IsNullOrWhiteSpace(_options.CodespaceSshConfigDirectory)
            ? Path.Combine(environment.ContentRootPath, "data", "codespaces-ssh")
            : _options.CodespaceSshConfigDirectory;

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

    private static bool IsCodespaceRunning(string state) =>
        state.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

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
        MixedProxyListener mixedListener,
        Process? tunnelProcess,
        string configPath,
        string accessLogPath,
        string errorLogPath,
        Guid? accountId,
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
        public string? ProxyUsername { get; } = proxyUsername;
        public string? ProxyPassword { get; } = proxyPassword;
        public int IdleShutdownMinutes { get; } = idleShutdownMinutes;
        public XrayProcessHandle Process { get; } = process;
        public MixedProxyListener MixedListener { get; } = mixedListener;
        public Process? TunnelProcess { get; } = tunnelProcess;
        public string ConfigPath { get; } = configPath;
        public string AccessLogPath { get; } = accessLogPath;
        public string ErrorLogPath { get; } = errorLogPath;
        public Guid? AccountId { get; } = accountId;
        public string? CodespaceName { get; } = codespaceName;
        public int? RemoteProxyPort { get; } = remoteProxyPort;
        public int? LocalTunnelPort { get; } = localTunnelPort;
        public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ProxyUsername) && !string.IsNullOrWhiteSpace(ProxyPassword);
        public bool IsStopped => _stopped;
        public DateTimeOffset LastActivityAt;
        public long TotalRequests;

        public void MarkStopped()
        {
            _stopped = true;
        }
    }
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
    DateTimeOffset IdleShutdownAt,
    DateTimeOffset? StoppedAt,
    string? LastError,
    long TotalRequests,
    long TotalConnectTunnels,
    long TotalBytesReceived,
    long TotalBytesSent,
    int ActiveConnections);

public sealed record LocalProxyRuntimeResult(bool Succeeded, string Message, LocalProxyRuntimeState? Session);

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
