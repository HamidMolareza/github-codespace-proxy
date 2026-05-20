using System.Buffers;
using System.Collections.Concurrent;
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
                    "A local proxy session is already running.",
                    Details: new { running.SessionId, running.LocalPort }),
                    cancellationToken);
                return LocalProxyStartResult.Ok("A local proxy session is already running.", await GetActiveAsync(cancellationToken));
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.LocalProxyProfiles.FirstOrDefaultAsync(x => x.Id == profileId, cancellationToken)
                ?? throw new InvalidOperationException("Local proxy profile was not found.");

            var listenerBindHost = string.IsNullOrWhiteSpace(_options.BindHostOverride)
                ? profile.BindHost
                : _options.BindHostOverride;
            var bindAddress = ResolveBindAddress(listenerBindHost);
            var listener = new TcpListener(bindAddress, profile.LocalPort);
            var session = new LocalProxySession
            {
                ProfileId = profile.Id,
                Status = LocalProxySessionStatus.Starting,
                BindHost = profile.BindHost,
                LocalPort = profile.LocalPort,
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
                $"Starting local proxy on {profile.BindHost}:{profile.LocalPort}.",
                Details: new { profile.Id, profile.BindHost, ListenerBindHost = listenerBindHost, profile.LocalPort }),
                cancellationToken);

            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                session.Status = LocalProxySessionStatus.Error;
                session.LastError = ex.Message;
                profile.Status = LocalProxyProfileStatus.Error;
                await db.SaveChangesAsync(cancellationToken);
                listener.Dispose();
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.port.unavailable",
                    OperationalEventSeverity.Error,
                    $"Local proxy port {profile.LocalPort} is unavailable.",
                    StandardError: ex.Message,
                    Details: new { profile.Id, profile.LocalPort }),
                    cancellationToken);
                return LocalProxyStartResult.Fail($"Port {profile.LocalPort} is unavailable: {ex.Message}", ToRuntimeState(session, profile, 0));
            }

            var password = string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword)
                ? null
                : secrets.Unprotect(profile.ProtectedProxyPassword);
            var state = new RunningLocalProxy(
                profile.Id,
                profile.Name,
                session.Id,
                profile.BindHost,
                profile.LocalPort,
                profile.ProxyUsername,
                password,
                Math.Max(1, profile.IdleShutdownMinutes),
                listener);
            state.LastActivityAt = session.LastActivityAt;
            _running = state;
            _ = Task.Run(() => AcceptLoopAsync(state), CancellationToken.None);

            session.Status = LocalProxySessionStatus.Running;
            profile.Status = LocalProxyProfileStatus.Running;
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.listener.started",
                OperationalEventSeverity.Information,
                $"Local proxy is listening on {profile.BindHost}:{profile.LocalPort}.",
                Details: new { ProfileId = profile.Id, SessionId = session.Id, profile.LocalPort }),
                cancellationToken);

            var probe = await ProbeActiveAsync(cancellationToken);
            return probe.Succeeded
                ? LocalProxyStartResult.Ok("Local proxy is ready.", probe.Session)
                : LocalProxyStartResult.Fail(probe.Message, probe.Session);
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
                    "Stop requested but no local proxy session was running."),
                    cancellationToken);
                return new LocalProxyRuntimeResult(true, "No local proxy session is running.", null);
            }

            running.Stop();
            await PersistStoppedAsync(running, reason, LocalProxySessionStatus.Stopped, cancellationToken);
            _running = null;
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.stop.completed",
                OperationalEventSeverity.Information,
                reason,
                Details: new { running.ProfileId, running.SessionId, running.LocalPort }),
                cancellationToken);
            return new LocalProxyRuntimeResult(true, reason, await ToResponseAsync(running, cancellationToken));
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

        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{running.LocalPort}"),
                UseProxy = true
            };
            if (running.RequiresAuthentication)
            {
                handler.Proxy.Credentials = new NetworkCredential(running.ProxyUsername, running.ProxyPassword);
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.ProbeUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.probe.success",
                OperationalEventSeverity.Information,
                $"Local proxy probe returned {(int)response.StatusCode}.",
                Details: new { running.ProfileId, running.SessionId, running.LocalPort, StatusCode = (int)response.StatusCode }),
                cancellationToken);
            return new LocalProxyRuntimeResult(true, "Local proxy probe succeeded.", await ToResponseAsync(running, cancellationToken));
        }
        catch (Exception ex)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.probe.failure",
                OperationalEventSeverity.Error,
                "Local proxy probe failed.",
                StandardError: ex.Message,
                Details: new { running.ProfileId, running.SessionId, running.LocalPort }),
                cancellationToken);
            await SetLastErrorAsync(running, ex.Message, cancellationToken);
            return new LocalProxyRuntimeResult(false, $"Local proxy probe failed: {ex.Message}", await ToResponseAsync(running, cancellationToken));
        }
    }

    public async Task<LocalProxyRuntimeState?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        return running is null || running.IsStopped ? null : await ToResponseAsync(running, cancellationToken);
    }

    public async Task StopIfIdleAsync(CancellationToken cancellationToken)
    {
        var running = _running;
        if (running is null || running.IsStopped)
        {
            return;
        }

        var idleFor = clock.UtcNow - running.LastActivityAt;
        if (idleFor < TimeSpan.FromMinutes(running.IdleShutdownMinutes))
        {
            return;
        }

        await StopAsync($"Stopped after {idleFor.TotalMinutes:n1} idle minutes.", cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "local_proxy.idle.timeout",
            OperationalEventSeverity.Information,
            "Stopped local proxy after idle timeout.",
            Details: new { running.ProfileId, running.SessionId, IdleMinutes = idleFor.TotalMinutes, running.IdleShutdownMinutes }),
            cancellationToken);
    }

    private async Task AcceptLoopAsync(RunningLocalProxy running)
    {
        try
        {
            while (!running.Cancellation.IsCancellationRequested)
            {
                var client = await running.Listener.AcceptTcpClientAsync(running.Cancellation);
                _ = Task.Run(() => HandleClientAsync(running, client), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local proxy accept loop failed.");
            await SetLastErrorAsync(running, ex.Message, CancellationToken.None);
            await PersistStoppedAsync(running, ex.Message, LocalProxySessionStatus.Error, CancellationToken.None);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.listener.failed",
                OperationalEventSeverity.Error,
                "Local proxy listener failed.",
                StandardError: ex.Message,
                Details: new { running.ProfileId, running.SessionId, running.LocalPort }),
                CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(RunningLocalProxy running, TcpClient client)
    {
        Interlocked.Increment(ref running.ActiveConnections);
        var bytesReceived = 0L;
        var bytesSent = 0L;
        string? target = null;
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var clientStream = client.GetStream();
            var head = await ReadRequestHeadAsync(clientStream, running.Cancellation);
            if (head is null)
            {
                return;
            }

            bytesReceived += head.TotalBytesRead;
            if (!IsAuthorized(running, head.Headers))
            {
                await WriteProxyAuthenticationRequiredAsync(clientStream, running.Cancellation);
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.auth.failed",
                    OperationalEventSeverity.Warning,
                    "Rejected unauthenticated local proxy request.",
                    Details: new { running.ProfileId, running.SessionId }),
                    CancellationToken.None);
                return;
            }

            if (head.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                target = head.Target;
                var connectResult = await HandleConnectAsync(running, clientStream, head, running.Cancellation);
                bytesReceived += connectResult.BytesReceived;
                bytesSent += connectResult.BytesSent;
                Interlocked.Increment(ref running.TotalConnectTunnels);
                return;
            }

            target = head.Target;
            var forwardResult = await HandleHttpAsync(clientStream, head, running.Cancellation);
            bytesReceived += forwardResult.BytesReceived;
            bytesSent += forwardResult.BytesSent;
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            await SetLastErrorAsync(running, ex.Message, CancellationToken.None);
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.request.failed",
                OperationalEventSeverity.Warning,
                "Local proxy request failed.",
                StandardError: ex.Message,
                Details: new { running.ProfileId, running.SessionId, Target = target }),
                CancellationToken.None);
        }
        finally
        {
            Interlocked.Decrement(ref running.ActiveConnections);
            await RecordRequestAsync(running, bytesReceived, bytesSent, CancellationToken.None);
        }
    }

    private static async Task<TransferResult> HandleConnectAsync(RunningLocalProxy running, NetworkStream clientStream, ProxyRequestHead head, CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostPort(head.Target, 443);
        using var target = new TcpClient();
        await target.ConnectAsync(host, port, cancellationToken);
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken);
        if (head.Leftover.Length > 0)
        {
            await target.GetStream().WriteAsync(head.Leftover, cancellationToken);
        }

        var targetStream = target.GetStream();
        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToTarget = CopyWithCountAsync(clientStream, targetStream, tunnelCts.Token);
        var targetToClient = CopyWithCountAsync(targetStream, clientStream, tunnelCts.Token);
        var completed = await Task.WhenAny(clientToTarget, targetToClient);
        tunnelCts.Cancel();
        var first = await completed;
        var second = 0L;
        try
        {
            second = completed == clientToTarget ? await targetToClient : await clientToTarget;
        }
        catch
        {
            // One side of a tunnel often closes first.
        }

        return new TransferResult(first + head.Leftover.Length, second);
    }

    private static async Task<TransferResult> HandleHttpAsync(NetworkStream clientStream, ProxyRequestHead head, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(head.Target, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            await WriteSimpleResponseAsync(clientStream, "400 Bad Request", "Only absolute http:// proxy requests are supported outside CONNECT.", cancellationToken);
            return new TransferResult(0, 0);
        }

        using var target = new TcpClient();
        await target.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 80, cancellationToken);
        var targetStream = target.GetStream();
        var requestBytes = RewriteHttpRequest(head, uri);
        await targetStream.WriteAsync(requestBytes, cancellationToken);
        if (head.Leftover.Length > 0)
        {
            await targetStream.WriteAsync(head.Leftover, cancellationToken);
        }

        var contentLength = GetContentLength(head.Headers);
        var remainingBodyBytes = Math.Max(0, contentLength - head.Leftover.Length);
        var uploaded = head.Leftover.Length + await CopyExactlyAsync(clientStream, targetStream, remainingBodyBytes, cancellationToken);
        var downloaded = await CopyWithCountAsync(targetStream, clientStream, cancellationToken);
        return new TransferResult(uploaded, downloaded);
    }

    private async Task RecordRequestAsync(RunningLocalProxy running, long bytesReceived, long bytesSent, CancellationToken cancellationToken)
    {
        running.LastActivityAt = clock.UtcNow;
        Interlocked.Increment(ref running.TotalRequests);
        Interlocked.Add(ref running.TotalBytesReceived, bytesReceived);
        Interlocked.Add(ref running.TotalBytesSent, bytesSent);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.LocalProxySessions.FirstOrDefaultAsync(x => x.Id == running.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.LastActivityAt = running.LastActivityAt;
        session.TotalRequests = running.TotalRequests;
        session.TotalConnectTunnels = running.TotalConnectTunnels;
        session.TotalBytesReceived = running.TotalBytesReceived;
        session.TotalBytesSent = running.TotalBytesSent;
        await db.SaveChangesAsync(cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "local_proxy.request.completed",
            OperationalEventSeverity.Debug,
            "Local proxy request completed.",
            Details: new { running.ProfileId, running.SessionId, bytesReceived, bytesSent }),
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
            session.TotalConnectTunnels = running.TotalConnectTunnels;
            session.TotalBytesReceived = running.TotalBytesReceived;
            session.TotalBytesSent = running.TotalBytesSent;
        }

        if (profile is not null)
        {
            profile.Status = status == LocalProxySessionStatus.Error ? LocalProxyProfileStatus.Error : LocalProxyProfileStatus.Stopped;
            profile.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetLastErrorAsync(RunningLocalProxy running, string error, CancellationToken cancellationToken)
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
        return ToRuntimeState(session, session.Profile!, running.ActiveConnections);
    }

    private static LocalProxyRuntimeState ToRuntimeState(LocalProxySession session, LocalProxyProfile profile, int activeConnections)
    {
        var idleAt = session.LastActivityAt.AddMinutes(Math.Max(1, profile.IdleShutdownMinutes));
        return new LocalProxyRuntimeState(
            session.Id,
            profile.Id,
            profile.Name,
            session.Status,
            session.BindHost,
            session.LocalPort,
            $"http://127.0.0.1:{session.LocalPort}",
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

    private static IPAddress ResolveBindAddress(string bindHost)
    {
        if (string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(bindHost, out var address) ? address : IPAddress.Loopback;
    }

    private static async Task<ProxyRequestHead?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var data = new MemoryStream();
            while (data.Length < 65536)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                data.Write(buffer, 0, read);
                var bytes = data.ToArray();
                var headerEnd = FindHeaderEnd(bytes);
                if (headerEnd < 0)
                {
                    continue;
                }

                var headerBytes = bytes[..headerEnd];
                var leftover = bytes[(headerEnd + 4)..];
                var headerText = Encoding.ASCII.GetString(headerBytes);
                var lines = headerText.Split("\r\n", StringSplitOptions.None);
                var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (requestLine.Length < 3)
                {
                    throw new FormatException("Invalid proxy request line.");
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':', StringComparison.Ordinal);
                    if (separator <= 0)
                    {
                        continue;
                    }

                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }

                return new ProxyRequestHead(requestLine[0], requestLine[1], requestLine[2], headers, leftover, bytes.Length);
            }

            throw new InvalidOperationException("Proxy request headers exceeded the 64 KB limit.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsAuthorized(RunningLocalProxy running, IReadOnlyDictionary<string, string> headers)
    {
        if (!running.RequiresAuthentication)
        {
            return true;
        }

        if (!headers.TryGetValue("Proxy-Authorization", out var value) ||
            !AuthenticationHeaderValue.TryParse(value, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        return string.Equals(decoded, $"{running.ProxyUsername}:{running.ProxyPassword}", StringComparison.Ordinal);
    }

    private static byte[] RewriteHttpRequest(ProxyRequestHead head, Uri uri)
    {
        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var builder = new StringBuilder();
        builder.Append(head.Method).Append(' ').Append(path).Append(' ').Append(head.Version).Append("\r\n");
        var hasHost = false;
        foreach (var (name, value) in head.Headers)
        {
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hasHost = true;
            }

            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (!hasHost)
        {
            builder.Append("Host: ").Append(uri.Authority).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static long GetContentLength(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue("Content-Length", out var value) && long.TryParse(value, out var length) ? length : 0;

    private static async Task<long> CopyExactlyAsync(Stream source, Stream destination, long byteCount, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var copied = 0L;
        try
        {
            while (copied < byteCount)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, byteCount - copied)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    private static async Task<long> CopyWithCountAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var copied = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    private static (string Host, int Port) ParseHostPort(string value, int defaultPort)
    {
        var parts = value.Split(':', 2);
        return parts.Length == 2 && int.TryParse(parts[1], out var port)
            ? (parts[0], port)
            : (value, defaultPort);
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (var index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task WriteProxyAuthenticationRequiredAsync(Stream stream, CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"GhProxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken);

    private static async Task WriteSimpleResponseAsync(Stream stream, string status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private sealed class RunningLocalProxy(
        Guid profileId,
        string profileName,
        Guid sessionId,
        string bindHost,
        int localPort,
        string? proxyUsername,
        string? proxyPassword,
        int idleShutdownMinutes,
        TcpListener listener)
    {
        private readonly CancellationTokenSource _cts = new();

        public Guid ProfileId { get; } = profileId;
        public string ProfileName { get; } = profileName;
        public Guid SessionId { get; } = sessionId;
        public string BindHost { get; } = bindHost;
        public int LocalPort { get; } = localPort;
        public string? ProxyUsername { get; } = proxyUsername;
        public string? ProxyPassword { get; } = proxyPassword;
        public int IdleShutdownMinutes { get; } = idleShutdownMinutes;
        public TcpListener Listener { get; } = listener;
        public CancellationToken Cancellation => _cts.Token;
        public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ProxyUsername) && !string.IsNullOrWhiteSpace(ProxyPassword);
        public bool IsStopped => _cts.IsCancellationRequested;
        public DateTimeOffset LastActivityAt;
        public long TotalRequests;
        public long TotalConnectTunnels;
        public long TotalBytesReceived;
        public long TotalBytesSent;
        public int ActiveConnections;

        public void Stop()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            Listener.Stop();
        }
    }

    private sealed record ProxyRequestHead(
        string Method,
        string Target,
        string Version,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Leftover,
        long TotalBytesRead);

    private sealed record TransferResult(long BytesReceived, long BytesSent);
}

public sealed record LocalProxyRuntimeState(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    LocalProxySessionStatus Status,
    string BindHost,
    int LocalPort,
    string ProxyUrl,
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
