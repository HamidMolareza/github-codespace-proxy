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

    private static ServiceProvider CreateProvider(
        string databasePath,
        FakeGitHubApiClient? github = null,
        IRuntimeToolChecker? toolChecker = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
        services.AddSingleton<IClock, TestClock>();
        services.AddSingleton<IOperationalEventSink, NoopOperationalEventSink>();
        services.AddSingleton<ICommandRunner, FakeCommandRunner>();
        services.AddSingleton<IGitHubApiClient>(github ?? new FakeGitHubApiClient());
        services.AddScoped<AuditService>();
        services.AddScoped<GitHubCodespaceService>();
        services.AddSingleton(toolChecker ?? new AvailableRuntimeToolChecker());
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

            return Task.FromResult<IReadOnlyList<GitHubCodespaceRemote>>([]);
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
