using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhProxy.Tests;

public sealed class LocalProxyRuntimeServiceTests
{
    [Fact]
    public void XrayConfigRenderer_AddsHttpAndSocksInboundsWithDirectOutbound()
    {
        var config = new XrayConfigRenderer().Render(new XrayConfigRequest(
            "127.0.0.1",
            8901,
            8902,
            "/tmp/access.log",
            "/tmp/error.log",
            "user",
            "pass"));

        using var document = JsonDocument.Parse(config);
        var root = document.RootElement;
        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToList();
        Assert.Contains(inbounds, x => x.GetProperty("protocol").GetString() == "http" && x.GetProperty("port").GetInt32() == 8901);
        var socks = inbounds.Single(x => x.GetProperty("protocol").GetString() == "socks");
        Assert.Equal(8902, socks.GetProperty("port").GetInt32());
        Assert.Equal("password", socks.GetProperty("settings").GetProperty("auth").GetString());
        Assert.Equal("freedom", root.GetProperty("outbounds")[0].GetProperty("protocol").GetString());
    }

    [Fact]
    public async Task StartAsync_ReturnsFailureWhenHttpPortIsUnavailable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath);
        var occupiedPort = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, occupiedPort);
        listener.Start();
        try
        {
            var profile = await CreateProfileAsync(provider, localPort: occupiedPort, socksPort: GetFreePort());
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartAsync(profile.Id, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("HTTP port", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_ReturnsFailureWhenSocksPortIsUnavailable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath);
        var occupiedPort = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, occupiedPort);
        listener.Start();
        try
        {
            var profile = await CreateProfileAsync(provider, localPort: GetFreePort(), socksPort: occupiedPort);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartAsync(profile.Id, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("SOCKS port", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            DeleteDatabase(databasePath);
        }
    }

    private static ServiceProvider CreateProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton<IClock, TestClock>();
        services.AddSingleton<IOperationalEventSink, NoopOperationalEventSink>();
        services.AddSingleton<XrayConfigRenderer>();
        services.AddSingleton<IXrayProcessRunner, ThrowingXrayProcessRunner>();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Path.GetTempPath()));
        services.AddLogging(builder => builder.AddDebug());
        services.Configure<LocalProxyOptions>(options => options.ProbeUrl = "http://example.com/");
        services.AddSingleton<LocalProxyRuntimeService>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return provider;
    }

    private static async Task<LocalProxyProfile> CreateProfileAsync(ServiceProvider provider, int localPort, int socksPort)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profile = new LocalProxyProfile
        {
            Name = $"test-{Guid.NewGuid():N}",
            BindHost = "127.0.0.1",
            LocalPort = localPort,
            SocksPort = socksPort,
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

    private sealed class ThrowingXrayProcessRunner : IXrayProcessRunner
    {
        public XrayProcessHandle Start(string executablePath, string configPath, string workingDirectory) =>
            throw new InvalidOperationException("The test should fail during port preflight.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "GhProxy.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
