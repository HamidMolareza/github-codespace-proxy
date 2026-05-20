using System.Net;
using System.Net.Sockets;
using System.Text;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GhProxy.Tests;

public sealed class LocalProxyRuntimeServiceTests
{
    [Fact]
    public async Task StartAsync_ForwardsHttpRequestsThroughLocalProxy()
    {
        await using var upstream = await LocalHttpServer.StartAsync();
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath, upstream.Url);
        try
        {
            var profile = await CreateProfileAsync(provider, localPort: GetFreePort());
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var started = await runtime.StartAsync(profile.Id, CancellationToken.None);

            Assert.True(started.Succeeded, started.Message);
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{profile.LocalPort}"),
                UseProxy = true
            };
            using var client = new HttpClient(handler);

            var response = await client.GetStringAsync(upstream.Url);

            Assert.Equal("ok", response);
            Assert.True(upstream.RequestCount >= 2);
            await runtime.StopAsync("test completed", CancellationToken.None);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_EnforcesProxyAuthenticationWhenConfigured()
    {
        await using var upstream = await LocalHttpServer.StartAsync();
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath, upstream.Url);
        try
        {
            var profile = await CreateProfileAsync(provider, localPort: GetFreePort(), username: "user", password: "pass");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();
            var started = await runtime.StartAsync(profile.Id, CancellationToken.None);
            Assert.True(started.Succeeded, started.Message);

            using var rejectedHandler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{profile.LocalPort}"),
                UseProxy = true
            };
            using var rejectedClient = new HttpClient(rejectedHandler);
            using var rejected = await rejectedClient.GetAsync(upstream.Url);
            Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, rejected.StatusCode);

            using var acceptedHandler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{profile.LocalPort}")
                {
                    Credentials = new NetworkCredential("user", "pass")
                },
                UseProxy = true
            };
            using var acceptedClient = new HttpClient(acceptedHandler);
            var body = await acceptedClient.GetStringAsync(upstream.Url);

            Assert.Equal("ok", body);
            await runtime.StopAsync("test completed", CancellationToken.None);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_ReturnsFailureWhenPortIsUnavailable()
    {
        await using var upstream = await LocalHttpServer.StartAsync();
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath, upstream.Url);
        var occupiedPort = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, occupiedPort);
        listener.Start();
        try
        {
            var profile = await CreateProfileAsync(provider, localPort: occupiedPort);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartAsync(profile.Id, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            DeleteDatabase(databasePath);
        }
    }

    private static ServiceProvider CreateProvider(string databasePath, string probeUrl)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton<IClock, TestClock>();
        services.AddSingleton<IOperationalEventSink, NoopOperationalEventSink>();
        services.AddLogging(builder => builder.AddDebug());
        services.Configure<LocalProxyOptions>(options => options.ProbeUrl = probeUrl);
        services.AddSingleton<LocalProxyRuntimeService>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return provider;
    }

    private static async Task<LocalProxyProfile> CreateProfileAsync(ServiceProvider provider, int localPort, string? username = null, string? password = null)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profile = new LocalProxyProfile
        {
            Name = $"test-{Guid.NewGuid():N}",
            BindHost = "127.0.0.1",
            LocalPort = localPort,
            ProxyUsername = username,
            ProtectedProxyPassword = password,
            IdleShutdownMinutes = 30,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.LocalProxyProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void DeleteDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _requestCount;

        private LocalHttpServer(TcpListener listener)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }
        public int RequestCount => _requestCount;

        public static Task<LocalHttpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LocalHttpServer(listener));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch
            {
                // The listener is expected to throw when disposed.
            }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _requestCount);
                var stream = client.GetStream();
                await ReadHeadersAsync(stream, _cts.Token);
                var body = Encoding.UTF8.GetBytes("ok");
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
            }
        }

        private static async Task ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            var data = new List<byte>();
            while (data.Count < 8192)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                data.AddRange(buffer.Take(read));
                if (Encoding.ASCII.GetString(data.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class NoopOperationalEventSink : IOperationalEventSink
    {
        public Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
