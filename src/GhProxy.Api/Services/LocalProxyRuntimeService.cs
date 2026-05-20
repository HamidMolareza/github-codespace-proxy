using System.ComponentModel;
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
    IHostEnvironment environment,
    ILogger<LocalProxyRuntimeService> logger)
{
    private readonly LocalProxyOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunningLocalProxy? _running;

    public async Task<LocalProxyStartResult> StartAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_running is { IsStopped: false } running)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.start.reused",
                    OperationalEventSeverity.Warning,
                    "A local Xray proxy session is already running.",
                    Details: new { running.SessionId, running.HttpPort, running.SocksPort }),
                    cancellationToken);
                return LocalProxyStartResult.Ok("A local Xray proxy session is already running.", await GetActiveAsync(cancellationToken));
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.LocalProxyProfiles.FirstOrDefaultAsync(x => x.Id == profileId, cancellationToken)
                ?? throw new InvalidOperationException("Local proxy profile was not found.");

            var listenerBindHost = string.IsNullOrWhiteSpace(_options.BindHostOverride)
                ? profile.BindHost
                : _options.BindHostOverride;
            var portError = TryGetUnavailablePortMessage(listenerBindHost, profile.LocalPort, profile.SocksPort);

            var session = new LocalProxySession
            {
                ProfileId = profile.Id,
                Status = LocalProxySessionStatus.Starting,
                BindHost = profile.BindHost,
                LocalPort = profile.LocalPort,
                SocksPort = profile.SocksPort,
                StartedAt = clock.UtcNow,
                LastActivityAt = clock.UtcNow
            };

            db.LocalProxySessions.Add(session);
            profile.Status = LocalProxyProfileStatus.Starting;
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.start.requested",
                OperationalEventSeverity.Information,
                $"Starting local Xray proxy on HTTP {profile.BindHost}:{profile.LocalPort} and SOCKS {profile.BindHost}:{profile.SocksPort}.",
                Details: new { profile.Id, profile.BindHost, ListenerBindHost = listenerBindHost, profile.LocalPort, profile.SocksPort }),
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
                    Details: new { profile.Id, profile.LocalPort, profile.SocksPort }),
                    cancellationToken);
                return LocalProxyStartResult.Fail(portError, ToRuntimeState(session, profile, 0));
            }

            var password = string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword)
                ? null
                : secrets.Unprotect(profile.ProtectedProxyPassword);
            var runtimeDirectory = GetRuntimeDirectory(session.Id);
            Directory.CreateDirectory(runtimeDirectory);
            var configPath = Path.Combine(runtimeDirectory, "config.json");
            var accessLogPath = Path.Combine(runtimeDirectory, "access.log");
            var errorLogPath = Path.Combine(runtimeDirectory, "error.log");
            var config = configRenderer.Render(new XrayConfigRequest(
                listenerBindHost,
                profile.LocalPort,
                profile.SocksPort,
                accessLogPath,
                errorLogPath,
                profile.ProxyUsername,
                password));
            await File.WriteAllTextAsync(configPath, config, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.xray.config.rendered",
                OperationalEventSeverity.Information,
                "Rendered Xray local proxy configuration.",
                Details: new { ProfileId = profile.Id, SessionId = session.Id, ConfigPath = configPath, AccessLogPath = accessLogPath, ErrorLogPath = errorLogPath }),
                cancellationToken);

            XrayProcessHandle handle;
            try
            {
                handle = processRunner.Start(_options.XrayExecutablePath, configPath, runtimeDirectory);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = ex.Message;
                profile.Status = LocalProxyProfileStatus.Error;
                await db.SaveChangesAsync(cancellationToken);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.xray.start.failed",
                    OperationalEventSeverity.Error,
                    "Failed to start Xray.",
                    StandardError: ex.Message,
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, _options.XrayExecutablePath }),
                    cancellationToken);
                return LocalProxyStartResult.Fail($"Failed to start Xray: {ex.Message}", ToRuntimeState(session, profile, 0));
            }

            var state = new RunningLocalProxy(
                profile.Id,
                profile.Name,
                session.Id,
                profile.BindHost,
                profile.LocalPort,
                profile.SocksPort,
                profile.ProxyUsername,
                password,
                Math.Max(1, profile.IdleShutdownMinutes),
                handle,
                configPath,
                accessLogPath,
                errorLogPath);
            state.LastActivityAt = session.LastActivityAt;
            _running = state;
            _ = Task.Run(() => MonitorProcessAsync(state), CancellationToken.None);

            if (!await WaitForPortsAsync(state, TimeSpan.FromSeconds(8), cancellationToken))
            {
                var message = string.IsNullOrWhiteSpace(handle.StandardError)
                    ? "Xray did not open the configured HTTP and SOCKS ports in time."
                    : $"Xray did not open the configured ports: {handle.StandardError}";
                state.MarkStopped();
                await handle.StopAsync();
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = message;
                profile.Status = LocalProxyProfileStatus.Error;
                await db.SaveChangesAsync(cancellationToken);
                _running = null;
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.xray.ready.timeout",
                    OperationalEventSeverity.Error,
                    "Xray failed to become ready.",
                    StandardError: handle.StandardError,
                    Details: new { ProfileId = profile.Id, SessionId = session.Id, profile.LocalPort, profile.SocksPort }),
                    cancellationToken);
                return LocalProxyStartResult.Fail(message, ToRuntimeState(session, profile, 0));
            }

            session.Status = LocalProxySessionStatus.Running;
            profile.Status = LocalProxyProfileStatus.Running;
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.xray.started",
                OperationalEventSeverity.Information,
                $"Local Xray proxy is listening on HTTP {profile.BindHost}:{profile.LocalPort} and SOCKS {profile.BindHost}:{profile.SocksPort}.",
                Details: new { ProfileId = profile.Id, SessionId = session.Id, handle.ProcessId, profile.LocalPort, profile.SocksPort }),
                cancellationToken);

            var probe = await ProbeActiveAsync(cancellationToken);
            return probe.Succeeded
                ? LocalProxyStartResult.Ok("Local Xray proxy is ready.", probe.Session)
                : LocalProxyStartResult.Ok($"Local Xray proxy is listening, but probe failed: {probe.Message}", probe.Session);
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
            await running.Process.StopAsync();
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

            await SetLastErrorAsync(running, running.Process.StandardError, CancellationToken.None);
            await PersistStoppedAsync(running, "Xray process exited unexpectedly.", LocalProxySessionStatus.Error, CancellationToken.None);
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

    private async Task<bool> WaitForPortsAsync(RunningLocalProxy running, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt && !running.Process.HasExited)
        {
            if (await CanConnectAsync(running.HttpPort, cancellationToken) &&
                await CanConnectAsync(running.SocksPort, cancellationToken))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
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
        return ToRuntimeState(session, session.Profile!, 0);
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

    private static string? TryGetUnavailablePortMessage(string bindHost, int httpPort, int socksPort)
    {
        if (httpPort == socksPort)
        {
            return "HTTP and SOCKS ports must be different.";
        }

        var bindAddress = ResolveBindAddress(bindHost);
        return IsPortAvailable(bindAddress, httpPort)
            ? IsPortAvailable(bindAddress, socksPort) ? null : $"SOCKS port {socksPort} is unavailable."
            : $"HTTP port {httpPort} is unavailable.";
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
        string? proxyUsername,
        string? proxyPassword,
        int idleShutdownMinutes,
        XrayProcessHandle process,
        string configPath,
        string accessLogPath,
        string errorLogPath)
    {
        private bool _stopped;

        public Guid ProfileId { get; } = profileId;
        public string ProfileName { get; } = profileName;
        public Guid SessionId { get; } = sessionId;
        public string BindHost { get; } = bindHost;
        public int HttpPort { get; } = httpPort;
        public int SocksPort { get; } = socksPort;
        public string? ProxyUsername { get; } = proxyUsername;
        public string? ProxyPassword { get; } = proxyPassword;
        public int IdleShutdownMinutes { get; } = idleShutdownMinutes;
        public XrayProcessHandle Process { get; } = process;
        public string ConfigPath { get; } = configPath;
        public string AccessLogPath { get; } = accessLogPath;
        public string ErrorLogPath { get; } = errorLogPath;
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
