using System.Net;
using System.Net.Sockets;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class LocalProxyGatewayService(
    IServiceScopeFactory scopeFactory,
    LocalProxyRuntimeService runtime,
    IClock clock,
    IOperationalEventSink events,
    ILogger<LocalProxyGatewayService> logger) : BackgroundService
{
    private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(5);
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
            await runtime.RecordGatewayRequestAsync(cancellationToken);
            var target = await runtime.GetOrStartGatewayTargetAsync(cancellationToken);
            var targetPort = firstByte == 0x05 ? target.InternalSocksPort : target.InternalHttpPort;
            using var upstream = new TcpClient();
            upstream.NoDelay = true;
            await upstream.ConnectAsync(IPAddress.Loopback, targetPort, cancellationToken);
            await using var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(firstByteBuffer, cancellationToken);

            var clientToUpstream = clientStream.CopyToAsync(upstreamStream, cancellationToken);
            var upstreamToClient = upstreamStream.CopyToAsync(clientStream, cancellationToken);
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Codespace proxy gateway relay closed.");
        }
        catch (Exception ex)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "local_proxy.gateway.relay_failed",
                OperationalEventSeverity.Error,
                "Codespace proxy gateway could not attach the request to an active Codespace proxy.",
                StandardError: ex.Message),
                CancellationToken.None);
            await TryWriteHttpUnavailableAsync(client, ex.Message);
        }
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
                IdleShutdownMinutes = 30,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.LocalProxyProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new GatewaySettings(profile.BindHost, profile.LocalPort);
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
