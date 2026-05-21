using System.Net;
using System.Net.Sockets;

namespace GhProxy.Api.Services;

public sealed class MixedProxyListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _httpPort;
    private readonly int _socksPort;
    private readonly Func<CancellationToken, Task>? _requestObserved;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private long _activeConnections;

    private MixedProxyListener(IPAddress bindAddress, int publicPort, int httpPort, int socksPort, Func<CancellationToken, Task>? requestObserved, ILogger logger)
    {
        _listener = new TcpListener(bindAddress, publicPort);
        _httpPort = httpPort;
        _socksPort = socksPort;
        _requestObserved = requestObserved;
        _logger = logger;
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int ActiveConnections => (int)Interlocked.Read(ref _activeConnections);

    public static MixedProxyListener Start(string bindHost, int publicPort, int httpPort, int socksPort, ILogger logger, Func<CancellationToken, Task>? requestObserved = null) =>
        new(ResolveBindAddress(bindHost), publicPort, httpPort, socksPort, requestObserved, logger);

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

            _ = Task.Run(() => HandleClientAsync(client, _stopping.Token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        using var clientDisposer = client;
        try
        {
            await using var clientStream = client.GetStream();
            var firstByte = clientStream.ReadByte();
            if (firstByte < 0)
            {
                return;
            }

            if (_requestObserved is not null)
            {
                await _requestObserved(cancellationToken);
            }

            var targetPort = firstByte == 0x05 ? _socksPort : _httpPort;
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, targetPort, cancellationToken);
            await using var upstreamStream = upstream.GetStream();
            upstreamStream.WriteByte((byte)firstByte);

            var clientToUpstream = clientStream.CopyToAsync(upstreamStream, cancellationToken);
            var upstreamToClient = upstreamStream.CopyToAsync(clientStream, cancellationToken);
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Mixed proxy relay closed.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
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
