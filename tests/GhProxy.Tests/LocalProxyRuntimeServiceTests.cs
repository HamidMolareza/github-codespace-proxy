using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GhProxy.Api.Contracts;
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
    public void XrayConfigRenderer_AddsHttpAndSocksInboundsWithCodespaceOutbound()
    {
        var config = new XrayConfigRenderer().Render(new XrayConfigRequest(
            "127.0.0.1",
            19001,
            19002,
            "/tmp/access.log",
            "/tmp/error.log",
            "user",
            "pass",
            new XrayOutboundProxy("http", "127.0.0.1", 19099)));

        using var document = JsonDocument.Parse(config);
        var root = document.RootElement;
        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToList();
        Assert.Contains(inbounds, x => x.GetProperty("protocol").GetString() == "http" && x.GetProperty("port").GetInt32() == 19001);
        var socks = inbounds.Single(x => x.GetProperty("protocol").GetString() == "socks");
        Assert.Equal(19002, socks.GetProperty("port").GetInt32());
        Assert.Equal("password", socks.GetProperty("settings").GetProperty("auth").GetString());
        var outbound = root.GetProperty("outbounds")[0];
        Assert.Equal("http", outbound.GetProperty("protocol").GetString());
        Assert.Equal(19099, outbound.GetProperty("settings").GetProperty("servers")[0].GetProperty("port").GetInt32());
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
            Assert.Contains("Proxy port", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_AllowsHttpAndSocksToShareOnePublicPort()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath);
        try
        {
            var port = GetFreePort();
            var profile = await CreateProfileAsync(provider, localPort: port, socksPort: port);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartAsync(profile.Id, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("Failed to start proxy", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartCodespaceProxyAsync_FailsBeforeGitHubStartWhenRuntimeToolsAreMissing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient();
        await using var provider = CreateProvider(databasePath, github, new MissingRuntimeToolChecker("gh"));
        try
        {
            var account = await CreateAccountAsync(provider);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartCodespaceProxyAsync(account.Id, "fresh", null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("gh", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, github.StartCalls);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task StartCodespaceProxyAsync_SchedulesRetryWhenGitHubRefreshFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient
        {
            ListCodespacesException = new HttpRequestException("network down")
        };
        await using var provider = CreateProvider(databasePath, github);
        try
        {
            var account = await CreateAccountAsync(provider);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.StartCodespaceProxyAsync(account.Id, "fresh", null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("network down", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, github.StartCalls);
            var status = runtime.GetAutomationStatus();
            Assert.Equal("Retrying", status.Phase);
            Assert.Equal(account.Id, status.AccountId);
            Assert.Equal("fresh", status.CodespaceName);
            Assert.NotNull(status.NextRetryAt);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RetryIfDueAsync_DoesNotScheduleOlderFailureWhenLatestCodespaceSessionStopped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient();
        await using var provider = CreateProvider(databasePath, github);
        try
        {
            var account = await CreateAccountAsync(provider);
            var profile = await CreateProfileAsync(provider, GetFreePort(), GetFreePort());
            var now = DateTimeOffset.UtcNow;
            await AddCodespaceSessionAsync(provider, profile, account.Id, LocalProxySessionStatus.Error, now.AddHours(-2), now.AddHours(-1), "restart failure");
            await AddCodespaceSessionAsync(provider, profile, account.Id, LocalProxySessionStatus.Stopped, now.AddMinutes(-30), now.AddMinutes(-5), null);
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            await runtime.RetryIfDueAsync(CancellationToken.None);

            Assert.Equal(0, github.StartCalls);
            Assert.Equal("WaitingForTraffic", runtime.GetAutomationStatus().Phase);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RetryIfDueAsync_SchedulesRetryWhenLatestCodespaceSessionIsFailed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient();
        await using var provider = CreateProvider(databasePath, github);
        try
        {
            var account = await CreateAccountAsync(provider);
            var profile = await CreateProfileAsync(provider, GetFreePort(), GetFreePort());
            var now = DateTimeOffset.UtcNow;
            await AddCodespaceSessionAsync(provider, profile, account.Id, LocalProxySessionStatus.Stopped, now.AddHours(-2), now.AddHours(-1), null);
            await AddCodespaceSessionAsync(provider, profile, account.Id, LocalProxySessionStatus.Error, now.AddMinutes(-30), now.AddMinutes(-5), "restart failure");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            await runtime.RetryIfDueAsync(CancellationToken.None);

            Assert.Equal(0, github.StartCalls);
            var status = runtime.GetAutomationStatus();
            Assert.Equal("Retrying", status.Phase);
            Assert.NotNull(status.NextRetryAt);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrStartGatewayTargetAsync_HoldsSingleRequestAfterIdleStop()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient();
        await using var provider = CreateProvider(databasePath, github);
        try
        {
            var account = await CreateAccountAsync(provider);
            await CreateIdleStoppedCodespaceSessionAsync(provider, account.Id, "space");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();
            DateTimeOffset originalLastActivityAt;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                originalLastActivityAt = (await db.LocalProxySessions.ToListAsync())
                    .OrderByDescending(x => x.StartedAt)
                    .Select(x => x.LastActivityAt)
                    .First();
            }

            var exception = await Assert.ThrowsAsync<LocalProxyIdleWakePendingException>(() =>
                runtime.GetOrStartGatewayTargetAsync(CancellationToken.None));

            Assert.Contains("4 more proxy request", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, github.StartCalls);
            var status = runtime.GetAutomationStatus();
            Assert.Equal("ZzzIdle", status.Phase);
            Assert.True(status.IdleWakePaused);
            Assert.Equal(1, status.IdleWakeRequestCount);
            Assert.Equal(5, status.IdleWakeRequestThreshold);
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var session = (await db.LocalProxySessions.ToListAsync()).OrderByDescending(x => x.StartedAt).First();
                Assert.Equal(originalLastActivityAt, session.LastActivityAt);
                Assert.NotEqual(originalLastActivityAt, session.LastRequestAt);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveAsync_DoesNotTreatXrayAccessLogAsUserActivity()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"gh-proxy-xray-{Guid.NewGuid():N}");
        var runner = new ListeningXrayProcessRunner();
        var clock = new TestClock(new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero));
        await using var provider = CreateProvider(
            databasePath,
            clock: clock,
            xrayRunner: runner,
            configureOptions: options =>
            {
                options.RequireHttpProbe = false;
                options.RequireSocksProbe = false;
                options.XrayConfigDirectory = runtimeDirectory;
            });
        try
        {
            var profile = await CreateProfileAsync(provider, GetFreePort(), GetFreePort());
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();
            var start = await runtime.StartAsync(profile.Id, CancellationToken.None);
            Assert.True(start.Succeeded, start.Message);
            var before = await runtime.GetActiveAsync(CancellationToken.None);
            Assert.NotNull(before);
            Assert.NotNull(runner.LastAccessLogPath);

            clock.UtcNow = clock.UtcNow.AddMinutes(10);
            await File.AppendAllTextAsync(runner.LastAccessLogPath!, "internal probe\n");
            File.SetLastWriteTimeUtc(runner.LastAccessLogPath!, clock.UtcNow.UtcDateTime);
            var after = await runtime.GetActiveAsync(CancellationToken.None);

            Assert.NotNull(after);
            Assert.Equal(before.LastActivityAt, after.LastActivityAt);
            Assert.Equal(before.LastRequestAt, after.LastRequestAt);
            Assert.Equal(before.TotalRequests, after.TotalRequests);
            await runtime.StopAsync("test cleanup", CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, recursive: true);
            }

            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GatewayRequestAndRelayActivity_UpdateSeparateUserActivityFields()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"gh-proxy-xray-{Guid.NewGuid():N}");
        var runner = new ListeningXrayProcessRunner();
        var clock = new TestClock(new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero));
        await using var provider = CreateProvider(
            databasePath,
            clock: clock,
            xrayRunner: runner,
            configureOptions: options =>
            {
                options.RequireHttpProbe = false;
                options.RequireSocksProbe = false;
                options.XrayConfigDirectory = runtimeDirectory;
            });
        try
        {
            var profile = await CreateProfileAsync(provider, GetFreePort(), GetFreePort());
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();
            var start = await runtime.StartAsync(profile.Id, CancellationToken.None);
            Assert.True(start.Succeeded, start.Message);

            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            await runtime.RecordGatewayRequestAsync(CancellationToken.None);
            var afterRequest = await runtime.GetActiveAsync(CancellationToken.None);
            Assert.NotNull(afterRequest);
            Assert.Equal(clock.UtcNow, afterRequest.LastActivityAt);
            Assert.Equal(clock.UtcNow, afterRequest.LastRequestAt);
            Assert.Equal(1, afterRequest.TotalRequests);

            var requestAt = clock.UtcNow;
            clock.UtcNow = clock.UtcNow.AddSeconds(20);
            await runtime.RecordGatewayRelayActivityAsync(CancellationToken.None);
            var afterRelay = await runtime.GetActiveAsync(CancellationToken.None);

            Assert.NotNull(afterRelay);
            Assert.Equal(clock.UtcNow, afterRelay.LastActivityAt);
            Assert.Equal(requestAt, afterRelay.LastRequestAt);
            Assert.Equal(1, afterRelay.TotalRequests);
            await runtime.StopAsync("test cleanup", CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, recursive: true);
            }

            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrStartGatewayTargetAsync_ResetsIdleWakeCounterAfterWindowExpires()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var clock = new TestClock(new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero));
        var github = new FakeGitHubApiClient();
        await using var provider = CreateProvider(
            databasePath,
            github,
            clock: clock,
            configureOptions: options =>
            {
                options.IdleWakeRequestThreshold = 2;
                options.IdleWakeWindowSeconds = 5;
            });
        try
        {
            var account = await CreateAccountAsync(provider);
            await CreateIdleStoppedCodespaceSessionAsync(provider, account.Id, "space");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            await Assert.ThrowsAsync<LocalProxyIdleWakePendingException>(() =>
                runtime.GetOrStartGatewayTargetAsync(CancellationToken.None));
            clock.UtcNow = clock.UtcNow.AddSeconds(6);
            await Assert.ThrowsAsync<LocalProxyIdleWakePendingException>(() =>
                runtime.GetOrStartGatewayTargetAsync(CancellationToken.None));

            Assert.Equal(0, github.StartCalls);
            var status = runtime.GetAutomationStatus();
            Assert.Equal(1, status.IdleWakeRequestCount);
            Assert.Equal(2, status.IdleWakeRequestThreshold);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetOrStartGatewayTargetAsync_ReleasesIdleWakeHoldAfterThreshold()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient
        {
            Codespaces =
            [
                new GitHubCodespaceRemote(
                    "space",
                    "Shutdown",
                    "octocat/proxy2",
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-10))
            ]
        };
        await using var provider = CreateProvider(
            databasePath,
            github,
            configureOptions: options => options.IdleWakeRequestThreshold = 2);
        try
        {
            var account = await CreateAccountAsync(provider);
            await CreateIdleStoppedCodespaceSessionAsync(provider, account.Id, "space");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            await Assert.ThrowsAsync<LocalProxyIdleWakePendingException>(() =>
                runtime.GetOrStartGatewayTargetAsync(CancellationToken.None));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.GetOrStartGatewayTargetAsync(CancellationToken.None));

            Assert.IsNotType<LocalProxyIdleWakePendingException>(exception);
            Assert.False(runtime.GetAutomationStatus().IdleWakePaused);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RetryCodespaceProxyAsync_BypassesIdleWakeThreshold()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var github = new FakeGitHubApiClient
        {
            Codespaces =
            [
                new GitHubCodespaceRemote(
                    "space",
                    "Shutdown",
                    "octocat/proxy2",
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-10))
            ]
        };
        await using var provider = CreateProvider(databasePath, github);
        try
        {
            var account = await CreateAccountAsync(provider);
            await CreateIdleStoppedCodespaceSessionAsync(provider, account.Id, "space");
            var runtime = provider.GetRequiredService<LocalProxyRuntimeService>();

            var result = await runtime.RetryCodespaceProxyAsync(CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(1, github.StartCalls);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public void TryGetFirstSshConfigHost_ReturnsFirstConcreteHost()
    {
        const string config = """
            Host *
              ForwardAgent no

            Host codespace-upgraded-winner
              HostName ssh.github.com
              User codespace

            Host other
              HostName example.invalid
            """;

        var host = LocalProxyRuntimeService.TryGetFirstSshConfigHost(config);

        Assert.Equal("codespace-upgraded-winner", host);
    }

    [Fact]
    public void TryGetFirstSshConfigHost_ReturnsNullWhenConfigHasNoConcreteHost()
    {
        const string config = """
            Host *
              ForwardAgent no
            """;

        var host = LocalProxyRuntimeService.TryGetFirstSshConfigHost(config);

        Assert.Null(host);
    }

    private static ServiceProvider CreateProvider(
        string databasePath,
        FakeGitHubApiClient? github = null,
        IRuntimeToolChecker? toolChecker = null,
        TestClock? clock = null,
        IXrayProcessRunner? xrayRunner = null,
        Action<LocalProxyOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton<IClock>(clock ?? new TestClock());
        services.AddSingleton<IOperationalEventSink, NoopOperationalEventSink>();
        services.AddSingleton<ICommandRunner, FakeCommandRunner>();
        services.AddSingleton<IGitHubApiClient>(github ?? new FakeGitHubApiClient());
        services.AddScoped<AuditService>();
        services.AddScoped<GitHubCodespaceService>();
        services.AddSingleton(toolChecker ?? new AvailableRuntimeToolChecker());
        services.AddSingleton<XrayConfigRenderer>();
        services.AddSingleton(xrayRunner ?? new ThrowingXrayProcessRunner());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Path.GetTempPath()));
        services.AddLogging(builder => builder.AddDebug());
        services.Configure<LocalProxyOptions>(options =>
        {
            options.ProbeUrl = "http://example.com/";
            configureOptions?.Invoke(options);
        });
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

    private static async Task<LocalProxySession> CreateIdleStoppedCodespaceSessionAsync(ServiceProvider provider, Guid accountId, string codespaceName)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profile = new LocalProxyProfile
        {
            Name = $"idle-{Guid.NewGuid():N}",
            BindHost = "127.0.0.1",
            LocalPort = GetFreePort(),
            SocksPort = GetFreePort(),
            IdleShutdownMinutes = 3,
            Status = LocalProxyProfileStatus.Stopped,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now
        };
        var session = new LocalProxySession
        {
            ProfileId = profile.Id,
            Status = LocalProxySessionStatus.Stopped,
            BindHost = profile.BindHost,
            LocalPort = profile.LocalPort,
            SocksPort = profile.SocksPort,
            StartedAt = now.AddMinutes(-8),
            LastActivityAt = now.AddMinutes(-4),
            LastRequestAt = now.AddMinutes(-4),
            StoppedAt = now,
            AccountId = accountId,
            CodespaceName = codespaceName,
            RemoteProxyPort = 8899
        };
        db.LocalProxyProfiles.Add(profile);
        db.LocalProxySessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<LocalProxySession> AddCodespaceSessionAsync(
        ServiceProvider provider,
        LocalProxyProfile profile,
        Guid accountId,
        LocalProxySessionStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset stoppedAt,
        string? lastError)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new LocalProxySession
        {
            ProfileId = profile.Id,
            Status = status,
            BindHost = profile.BindHost,
            LocalPort = profile.LocalPort,
            SocksPort = profile.SocksPort,
            StartedAt = startedAt,
            LastActivityAt = stoppedAt,
            LastRequestAt = stoppedAt,
            StoppedAt = stoppedAt,
            LastError = lastError,
            AccountId = accountId,
            CodespaceName = "space",
            RemoteProxyPort = 8899
        };
        db.LocalProxySessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<GitHubAccount> CreateAccountAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var account = new GitHubAccount
        {
            DisplayName = "Primary",
            Username = "octocat",
            ProtectedPersonalAccessToken = "token",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.GitHubAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
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
        public TestClock()
        {
            UtcNow = DateTimeOffset.UtcNow;
        }

        public TestClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
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

    private sealed class ListeningXrayProcessRunner : IXrayProcessRunner
    {
        private readonly List<TcpListener> _listeners = [];
        private readonly List<Task> _acceptTasks = [];
        private CancellationTokenSource? _stopping;
        private bool _stopped;

        public string? LastAccessLogPath { get; private set; }

        public XrayProcessHandle Start(string executablePath, string configPath, string workingDirectory)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            LastAccessLogPath = document.RootElement.GetProperty("log").GetProperty("access").GetString();
            if (!string.IsNullOrWhiteSpace(LastAccessLogPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastAccessLogPath)!);
                File.WriteAllText(LastAccessLogPath, string.Empty);
            }

            _stopping = new CancellationTokenSource();
            foreach (var inbound in document.RootElement.GetProperty("inbounds").EnumerateArray())
            {
                var port = inbound.GetProperty("port").GetInt32();
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listeners.Add(listener);
                _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(listener, _stopping.Token)));
            }

            return new XrayProcessHandle(
                Environment.ProcessId,
                () => _stopped,
                StopAsync,
                Task.WhenAll(_acceptTasks),
                () => string.Empty);
        }

        private async Task StopAsync()
        {
            _stopped = true;
            _stopping?.Cancel();
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            try
            {
                await Task.WhenAll(_acceptTasks);
            }
            catch
            {
                // Listener shutdown closes pending accepts.
            }
        }

        private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using var clientDisposer = client;
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch
                    {
                        // Test listener only needs to accept connections.
                    }
                }, CancellationToken.None);
            }
        }
    }

    private sealed class AvailableRuntimeToolChecker : IRuntimeToolChecker
    {
        public RuntimeToolCheckResult CheckCodespaceProxyTools(string xrayExecutablePath) =>
            new(GetRuntimeDiagnostics(xrayExecutablePath));

        public IReadOnlyList<RuntimeToolDiagnostic> GetRuntimeDiagnostics(string xrayExecutablePath) =>
        [
            new("Xray", xrayExecutablePath, true, xrayExecutablePath),
            new("GitHub CLI", "gh", true, "gh"),
            new("ssh", "ssh", true, "ssh")
        ];
    }

    private sealed class MissingRuntimeToolChecker(string missingCommand) : IRuntimeToolChecker
    {
        public RuntimeToolCheckResult CheckCodespaceProxyTools(string xrayExecutablePath) =>
            new(GetRuntimeDiagnostics(xrayExecutablePath));

        public IReadOnlyList<RuntimeToolDiagnostic> GetRuntimeDiagnostics(string xrayExecutablePath) =>
        [
            new("Xray", xrayExecutablePath, true, xrayExecutablePath),
            new("GitHub CLI", "gh", missingCommand != "gh", missingCommand == "gh" ? "Missing executable: gh" : "gh"),
            new("ssh", "ssh", missingCommand != "ssh", missingCommand == "ssh" ? "Missing executable: ssh" : "ssh")
        ];
    }

    private sealed class FakeGitHubApiClient : IGitHubApiClient
    {
        public Exception? ListCodespacesException { get; init; }
        public IReadOnlyList<GitHubCodespaceRemote> Codespaces { get; init; } = [];
        public int StartCalls { get; private set; }

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile("octocat"));

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken)
        {
            if (ListCodespacesException is not null)
            {
                throw ListCodespacesException;
            }

            return Task.FromResult(Codespaces);
        }

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            StartCalls++;
            throw new NotSupportedException();
        }

        public Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", null, null, null, "https://github.com/settings/billing/usage", []));
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "GhProxy.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
