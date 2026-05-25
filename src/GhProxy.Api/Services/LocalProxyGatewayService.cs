using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class LocalProxyGatewayService(
    IServiceScopeFactory scopeFactory,
    LocalProxyRuntimeService runtime,
    IClock clock,
    IOperationalEventSink events,
    IOptions<LocalProxyOptions> options,
    ILogger<LocalProxyGatewayService> logger) : BackgroundService
{
    private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(5);
    internal const int RequestHistoryRetentionLimit = 500;
    private const int MaxInitialHttpBytes = 16 * 1024;
    private const int MaxObservedSocksBytes = 512;
    private readonly LocalProxyOptions _options = options.Value;
    private GatewayBinding? _binding;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await LoadSettingsAsync(stoppingToken);
                if (_binding is null || !_binding.Matches(settings))
                {
                    if (_binding is not null)
                    {
                        await _binding.DisposeAsync();
                    }

                    _binding = GatewayBinding.Start(settings, HandleClientAsync, logger);
                    await events.WriteAsync(new OperationalEventWrite(
                        "local_proxy.gateway.listening",
                        OperationalEventSeverity.Information,
                        $"Codespace proxy gateway is listening on {settings.BindHost}:{settings.LocalPort}.",
                        Details: new { settings.BindHost, settings.LocalPort }),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await events.WriteAsync(new OperationalEventWrite(
                    "local_proxy.gateway.start_failed",
                    OperationalEventSeverity.Error,
                    "Could not start the Codespace proxy gateway.",
                    StandardError: ex.Message),
                    CancellationToken.None);
            }

            await Task.Delay(SettingsPollInterval, stoppingToken);
        }

        if (_binding is not null)
        {
            await _binding.DisposeAsync();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientDisposer = client;
        var isSocksRequest = false;
        var stopwatch = Stopwatch.StartNew();
        var observedAt = clock.UtcNow;
        GatewayRequestTarget? requestTarget = null;
        Guid? requestId = null;
        LocalProxyGatewayTarget? gatewayTarget = null;
        var protocol = "HTTP";
        try
        {
            client.NoDelay = true;
            await using var clientStream = client.GetStream();
            var firstByteBuffer = new byte[1];
            var read = await clientStream.ReadAsync(firstByteBuffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            var firstByte = firstByteBuffer[0];
            isSocksRequest = firstByte == 0x05;
            protocol = isSocksRequest ? "SOCKS5" : "HTTP";
            var initialPayload = isSocksRequest
                ? firstByteBuffer
                : await ReadHttpInitialPayloadAsync(clientStream, firstByte, cancellationToken);
            if (!isSocksRequest)
            {
                requestTarget = TryParseHttpTarget(initialPayload, out var httpTarget) ? httpTarget : null;
            }

            gatewayTarget = await runtime.GetOrStartGatewayTargetAsync(cancellationToken);
            await runtime.RecordGatewayRequestAsync(cancellationToken);
            var targetPort = isSocksRequest ? gatewayTarget.InternalSocksPort : gatewayTarget.InternalHttpPort;
            using var upstream = new TcpClient();
            upstream.NoDelay = true;
            await upstream.ConnectAsync(IPAddress.Loopback, targetPort, cancellationToken);
            await using var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(initialPayload, cancellationToken);
            var socksObserver = isSocksRequest ? new SocksTargetObserver() : null;
            if (socksObserver is not null)
            {
                socksObserver.Capture(initialPayload);
                requestTarget = socksObserver.Target;
            }

            requestId = await InsertGatewayRequestAsync(
                observedAt,
                protocol,
                requestTarget,
                "Forwarded",
                gatewayTarget,
                null,
                null,
                cancellationToken);

            var clientToUpstream = RelayAsync(
                clientStream,
                upstreamStream,
                socksObserver,
                target => UpdateGatewayRequestAsync(requestId, "Forwarded", target, null, stopwatch.ElapsedMilliseconds, CancellationToken.None),
                cancellationToken);
            var upstreamToClient = RelayAsync(upstreamStream, clientStream, cancellationToken);
            await Task.WhenAny(clientToUpstream, upstreamToClient);
            requestTarget = socksObserver?.Target ?? requestTarget;
            await UpdateGatewayRequestAsync(requestId, "Forwarded", requestTarget, null, stopwatch.ElapsedMilliseconds, CancellationToken.None);
        }
        catch (LocalProxyIdleWakePendingException ex)
        {
            logger.LogDebug(ex, "Codespace proxy gateway held request while idle wake threshold is pending.");
            await InsertGatewayRequestAsync(
                observedAt,
                protocol,
                requestTarget,
                "WakePending",
                gatewayTarget,
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                CancellationToken.None);
            if (!isSocksRequest)
            {
                await TryWriteHttpUnavailableAsync(client, ex.Message);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Codespace proxy gateway relay closed.");
            if (requestId is null && ex is SocketException)
            {
                await InsertGatewayRequestAsync(
                    observedAt,
                    protocol,
                    requestTarget,
                    "Failed",
                    gatewayTarget,
                    ex.Message,
                    stopwatch.ElapsedMilliseconds,
                    CancellationToken.None);
            }
            else if (requestId is not null)
            {
                await UpdateGatewayRequestAsync(requestId, "Forwarded", requestTarget, null, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            if (requestId is null)
            {
                await InsertGatewayRequestAsync(
                    observedAt,
                    protocol,
                    requestTarget,
                    "Failed",
                    gatewayTarget,
                    ex.Message,
                    stopwatch.ElapsedMilliseconds,
                    CancellationToken.None);
            }
            else
            {
                await UpdateGatewayRequestAsync(requestId, "Failed", requestTarget, ex.Message, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            }

            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.gateway.relay_failed",
                OperationalEventSeverity.Error,
                "Codespace proxy gateway could not attach the request to an active Codespace proxy.",
                StandardError: ex.Message),
                CancellationToken.None);
            await TryWriteHttpUnavailableAsync(client, ex.Message);
        }
    }

    private async Task RelayAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        await RelayAsync(source, destination, null, null, cancellationToken);
    }

    private async Task RelayAsync(
        Stream source,
        Stream destination,
        SocksTargetObserver? socksObserver,
        Func<GatewayRequestTarget, Task>? onSocksTargetFound,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            var hadSocksTarget = socksObserver?.Target is not null;
            socksObserver?.Capture(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (!hadSocksTarget && socksObserver?.Target is not null && onSocksTargetFound is not null)
            {
                await onSocksTargetFound(socksObserver.Target);
            }

            await runtime.RecordGatewayRelayActivityAsync(cancellationToken);
        }
    }

    private async Task<Guid> InsertGatewayRequestAsync(
        DateTimeOffset observedAt,
        string protocol,
        GatewayRequestTarget? target,
        string outcome,
        LocalProxyGatewayTarget? gatewayTarget,
        string? errorMessage,
        long? durationMs,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new LocalProxyGatewayRequest
        {
            ObservedAt = observedAt,
            Protocol = protocol,
            TargetHost = target?.Host,
            TargetPort = target?.Port,
            Outcome = outcome,
            SessionId = gatewayTarget?.SessionId,
            AccountId = gatewayTarget?.AccountId,
            CodespaceName = gatewayTarget?.CodespaceName,
            ErrorMessage = Truncate(errorMessage, 1000),
            DurationMs = durationMs
        };
        db.LocalProxyGatewayRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        await TrimRequestHistoryAsync(db, RequestHistoryRetentionLimit, cancellationToken);
        return request.Id;
    }

    private async Task UpdateGatewayRequestAsync(
        Guid? requestId,
        string outcome,
        GatewayRequestTarget? target,
        string? errorMessage,
        long durationMs,
        CancellationToken cancellationToken)
    {
        if (requestId is null)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.LocalProxyGatewayRequests.FirstOrDefaultAsync(x => x.Id == requestId.Value, cancellationToken);
        if (request is null)
        {
            return;
        }

        request.Outcome = outcome;
        if (target is not null)
        {
            request.TargetHost = target.Host;
            request.TargetPort = target.Port;
        }

        request.ErrorMessage = Truncate(errorMessage, 1000);
        request.DurationMs = durationMs;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task TrimRequestHistoryAsync(AppDbContext db, int retentionLimit, CancellationToken cancellationToken)
    {
        var idsToDelete = (await db.LocalProxyGatewayRequests.ToListAsync(cancellationToken))
            .OrderByDescending(x => x.ObservedAt)
            .Skip(Math.Max(0, retentionLimit))
            .Select(x => x.Id)
            .ToHashSet();
        if (idsToDelete.Count == 0)
        {
            return;
        }

        var oldRequests = await db.LocalProxyGatewayRequests
            .Where(x => idsToDelete.Contains(x.Id))
            .ToListAsync(cancellationToken);
        db.LocalProxyGatewayRequests.RemoveRange(oldRequests);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<GatewaySettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profiles = await db.LocalProxyProfiles.ToListAsync(cancellationToken);
        var profile = profiles.OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (profile is null)
        {
            profile = new LocalProxyProfile
            {
                Name = "Default",
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                SocksPort = 8910,
                IdleShutdownMinutes = Math.Clamp(_options.DefaultIdleShutdownMinutes, 1, 1440),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.LocalProxyProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new GatewaySettings(profile.BindHost, profile.LocalPort);
    }

    private static async Task<byte[]> ReadHttpInitialPayloadAsync(Stream stream, byte firstByte, CancellationToken cancellationToken)
    {
        var payload = new List<byte>(1024) { firstByte };
        var buffer = new byte[1024];
        while (payload.Count < MaxInitialHttpBytes && !HasHttpHeaderTerminator(payload))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxInitialHttpBytes - payload.Count)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            payload.AddRange(buffer.Take(read));
        }

        return payload.ToArray();
    }

    internal static bool TryParseHttpTarget(ReadOnlySpan<byte> payload, out GatewayRequestTarget target)
    {
        target = default!;
        var text = Encoding.ASCII.GetString(payload);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstLine = lines.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase) &&
                    TryParseHostPort(parts[1], 443, out target))
                {
                    return true;
                }

                if (Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) &&
                    !string.IsNullOrWhiteSpace(uri.Host))
                {
                    target = new GatewayRequestTarget(uri.Host, uri.IsDefaultPort ? null : uri.Port);
                    return true;
                }
            }
        }

        var hostLine = lines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        if (hostLine is null)
        {
            return false;
        }

        return TryParseHostPort(hostLine["Host:".Length..].Trim(), null, out target);
    }

    internal static bool TryParseSocksTarget(ReadOnlySpan<byte> payload, out GatewayRequestTarget target)
    {
        target = default!;
        if (payload.Length < 5 || payload[0] != 0x05)
        {
            return false;
        }

        var methodsLength = payload[1];
        var requestOffset = 2 + methodsLength;
        if (payload.Length <= requestOffset + 6 || payload[requestOffset] != 0x05)
        {
            return false;
        }

        var addressOffset = requestOffset + 4;
        string host;
        int portOffset;
        switch (payload[requestOffset + 3])
        {
            case 0x01:
                if (payload.Length < addressOffset + 4 + 2)
                {
                    return false;
                }

                host = new IPAddress(payload.Slice(addressOffset, 4)).ToString();
                portOffset = addressOffset + 4;
                break;
            case 0x03:
                if (payload.Length <= addressOffset)
                {
                    return false;
                }

                var hostLength = payload[addressOffset];
                if (payload.Length < addressOffset + 1 + hostLength + 2)
                {
                    return false;
                }

                host = Encoding.ASCII.GetString(payload.Slice(addressOffset + 1, hostLength));
                portOffset = addressOffset + 1 + hostLength;
                break;
            case 0x04:
                if (payload.Length < addressOffset + 16 + 2)
                {
                    return false;
                }

                host = new IPAddress(payload.Slice(addressOffset, 16)).ToString();
                portOffset = addressOffset + 16;
                break;
            default:
                return false;
        }

        var port = (payload[portOffset] << 8) | payload[portOffset + 1];
        target = new GatewayRequestTarget(host, port);
        return true;
    }

    private static bool TryParseHostPort(string value, int? defaultPort, out GatewayRequestTarget target)
    {
        target = default!;
        var host = value.Trim();
        int? port = defaultPort;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.StartsWith('['))
        {
            var closeBracket = host.IndexOf(']');
            if (closeBracket <= 0)
            {
                return false;
            }

            var portText = closeBracket + 2 <= host.Length && host[closeBracket + 1] == ':'
                ? host[(closeBracket + 2)..]
                : null;
            host = host[1..closeBracket];
            if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText, out var parsedPort))
            {
                port = parsedPort;
            }
        }
        else
        {
            var lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && host.IndexOf(':') == lastColon)
            {
                var portText = host[(lastColon + 1)..];
                if (int.TryParse(portText, out var parsedPort))
                {
                    port = parsedPort;
                    host = host[..lastColon];
                }
            }
        }

        target = new GatewayRequestTarget(host, port);
        return true;
    }

    private static bool HasHttpHeaderTerminator(IReadOnlyList<byte> payload)
    {
        for (var index = 3; index < payload.Count; index++)
        {
            if (payload[index - 3] == '\r' &&
                payload[index - 2] == '\n' &&
                payload[index - 1] == '\r' &&
                payload[index] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static async Task TryWriteHttpUnavailableAsync(TcpClient client, string message)
    {
        try
        {
            if (!client.Connected)
            {
                return;
            }

            var body = $"Codespace proxy is unavailable: {message}";
            var payload = System.Text.Encoding.UTF8.GetBytes(
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" +
                body);
            await client.GetStream().WriteAsync(payload);
        }
        catch
        {
            // Best-effort diagnostic response for HTTP clients.
        }
    }

    private sealed record GatewaySettings(string BindHost, int LocalPort);

    internal sealed record GatewayRequestTarget(string Host, int? Port);

    private sealed class SocksTargetObserver
    {
        private readonly List<byte> _payload = new(MaxObservedSocksBytes);

        public GatewayRequestTarget? Target { get; private set; }

        public void Capture(ReadOnlySpan<byte> payload)
        {
            if (Target is not null || _payload.Count >= MaxObservedSocksBytes)
            {
                return;
            }

            var available = Math.Min(payload.Length, MaxObservedSocksBytes - _payload.Count);
            for (var index = 0; index < available; index++)
            {
                _payload.Add(payload[index]);
            }

            if (TryParseSocksTarget(_payload.ToArray(), out var target))
            {
                Target = target;
            }
        }
    }

    private sealed class GatewayBinding : IAsyncDisposable
    {
        private readonly GatewaySettings _settings;
        private readonly TcpListener _listener;
        private readonly Func<TcpClient, CancellationToken, Task> _handler;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;

        private GatewayBinding(GatewaySettings settings, Func<TcpClient, CancellationToken, Task> handler, ILogger logger)
        {
            _settings = settings;
            _handler = handler;
            _logger = logger;
            _listener = new TcpListener(ResolveBindAddress(settings.BindHost), settings.LocalPort);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static GatewayBinding Start(GatewaySettings settings, Func<TcpClient, CancellationToken, Task> handler, ILogger logger) =>
            new(settings, handler, logger);

        public bool Matches(GatewaySettings settings) =>
            string.Equals(_settings.BindHost, settings.BindHost, StringComparison.OrdinalIgnoreCase) &&
            _settings.LocalPort == settings.LocalPort;

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Listener shutdown closes pending accepts and active relays best-effort.
            }

            _stopping.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => _handler(client, _stopping.Token), CancellationToken.None);
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
    }
}
