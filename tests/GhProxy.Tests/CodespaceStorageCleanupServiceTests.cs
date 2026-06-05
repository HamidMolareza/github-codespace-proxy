using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GhProxy.Tests;

public sealed class CodespaceStorageCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_DeletesStoppedProxyCodespaceWhenStorageQuotaIsLimited()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var account = await AddAccountAsync(db, now);
            var proxy = AddSnapshot(db, account.Id, "storage-cost", "Shutdown", "octocat/proxy2", now);
            AddSnapshot(db, account.Id, "manual", "Shutdown", "octocat/manual", now);
            AddSnapshot(db, account.Id, "running", "Available", "octocat/proxy2", now);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            var service = CreateService(db, github);

            var result = await service.CleanupAsync(account, StorageLimitedUsage(), [.. db.CodespaceSnapshots], CancellationToken.None);

            Assert.Equal(1, result.DeletedCount);
            Assert.Contains(("token", proxy.Name), github.DeleteCalls);
            Assert.DoesNotContain(result.Snapshots, x => x.Name == proxy.Name);
            Assert.True(await db.CodespaceSnapshots.AnyAsync(x => x.Name == "manual"));
            Assert.True(await db.CodespaceSnapshots.AnyAsync(x => x.Name == "running"));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task CleanupAsync_DoesNotDeleteWhenOnlyComputeQuotaIsLimited()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var account = await AddAccountAsync(db, now);
            AddSnapshot(db, account.Id, "storage-cost", "Shutdown", "octocat/proxy2", now);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            var service = CreateService(db, github);
            var usage = new GitHubUsageResponse(
                GitHubAccountQuotaState.Limited,
                "compute limited",
                null,
                null,
                null,
                "billing",
                [
                    new GitHubUsageQuotaSummaryResponse("Compute", 120, 120, 0, 100, "core hours"),
                    new GitHubUsageQuotaSummaryResponse("Storage", 2, 15, 13, 13.3m, "GB-month")
                ]);

            var result = await service.CleanupAsync(account, usage, [.. db.CodespaceSnapshots], CancellationToken.None);

            Assert.Equal(0, result.DeletedCount);
            Assert.Empty(github.DeleteCalls);
            Assert.Single(result.Snapshots);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task CleanupAsync_RespectsDisabledOption()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var account = await AddAccountAsync(db, now);
            AddSnapshot(db, account.Id, "storage-cost", "Shutdown", "octocat/proxy2", now);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            var service = CreateService(db, github, autoDelete: false);

            var result = await service.CleanupAsync(account, StorageLimitedUsage(), [.. db.CodespaceSnapshots], CancellationToken.None);

            Assert.Equal(0, result.DeletedCount);
            Assert.Empty(github.DeleteCalls);
            Assert.Single(result.Snapshots);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task CleanupAsync_DoesNotDeleteCodespaceUsedByRunningSession()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var account = await AddAccountAsync(db, now);
            AddSnapshot(db, account.Id, "storage-cost", "Shutdown", "octocat/proxy2", now);
            db.LocalProxyProfiles.Add(new LocalProxyProfile
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Name = "Default",
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                SocksPort = 8910,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.LocalProxySessions.Add(new LocalProxySession
            {
                ProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Status = LocalProxySessionStatus.Running,
                BindHost = "127.0.0.1",
                LocalPort = 8910,
                SocksPort = 8910,
                StartedAt = now,
                LastActivityAt = now,
                AccountId = account.Id,
                CodespaceName = "storage-cost"
            });
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            var service = CreateService(db, github);

            var result = await service.CleanupAsync(account, StorageLimitedUsage(), [.. db.CodespaceSnapshots], CancellationToken.None);

            Assert.Equal(0, result.DeletedCount);
            Assert.Empty(github.DeleteCalls);
            Assert.Single(result.Snapshots);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task MaintenanceRunOnceAsync_DeletesStorageLimitedStoppedProxyCodespace()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var github = new FakeGitHubApiClient
            {
                Codespaces =
                [
                    new GitHubCodespaceRemote(
                        "storage-cost",
                        "Shutdown",
                        "octocat/proxy2",
                        "2-core",
                        "UsEast",
                        null,
                        "octocat",
                        now.AddHours(-3),
                        now.AddHours(-2),
                        now.AddHours(-1))
                ]
            };
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
            services.AddSingleton<IGitHubApiClient>(github);
            services.AddSingleton<IClock, TestClock>();
            services.AddSingleton<ISecretProtector, PassThroughSecretProtector>();
            services.AddSingleton<IOperationalEventSink, NoopOperationalEventSink>();
            services.AddScoped<AuditService>();
            services.AddScoped<DatabaseSchemaInitializer>();
            services.AddScoped<GitHubCodespaceService>();
            services.AddScoped<CodespaceStorageCleanupService>();
            services.Configure<GitHubOptions>(options => options.AutoDeleteStorageLimitedProxyCodespaces = true);
            services.Configure<LocalProxyOptions>(_ => { });
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
                await AddAccountAsync(db, now);
            }

            var maintenance = new GitHubCodespaceMaintenanceService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GitHubOptions { AutoDeleteStorageLimitedProxyCodespaces = true }),
                provider.GetRequiredService<IClock>(),
                NullLogger<GitHubCodespaceMaintenanceService>.Instance);

            await maintenance.RunOnceAsync(CancellationToken.None);

            Assert.Contains(("token", "storage-cost"), github.DeleteCalls);
            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await verifyDb.CodespaceSnapshots.AnyAsync(x => x.Name == "storage-cost"));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static CodespaceStorageCleanupService CreateService(AppDbContext db, IGitHubApiClient github, bool autoDelete = true)
    {
        var clock = new TestClock();
        var events = new NoopOperationalEventSink();
        var codespaces = new GitHubCodespaceService(db, github, new PassThroughSecretProtector(), clock, new AuditService(db, clock, events), events);
        return new CodespaceStorageCleanupService(
            db,
            codespaces,
            events,
            Options.Create(new GitHubOptions { AutoDeleteStorageLimitedProxyCodespaces = autoDelete }),
            Options.Create(new LocalProxyOptions()));
    }

    private static async Task<GitHubAccount> AddAccountAsync(AppDbContext db, DateTimeOffset now)
    {
        var account = new GitHubAccount
        {
            DisplayName = "Octocat",
            Username = "octocat",
            ProtectedPersonalAccessToken = "token",
            ValidationStatus = GitHubAccountValidationStatus.Valid,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.GitHubAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static CodespaceSnapshot AddSnapshot(AppDbContext db, Guid accountId, string name, string state, string repositoryFullName, DateTimeOffset now)
    {
        var snapshot = new CodespaceSnapshot
        {
            AccountId = accountId,
            Name = name,
            State = state,
            RepositoryFullName = repositoryFullName,
            LastSyncedAt = now
        };
        db.CodespaceSnapshots.Add(snapshot);
        return snapshot;
    }

    private static GitHubUsageResponse StorageLimitedUsage() =>
        new(
            GitHubAccountQuotaState.Limited,
            "storage limited",
            null,
            null,
            null,
            "billing",
            [new GitHubUsageQuotaSummaryResponse("Storage", 15, 15, 0, 100, "GB-month")]);

    private static AppDbContext CreateDb(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new AppDbContext(options);
    }

    private sealed class FakeGitHubApiClient : IGitHubApiClient
    {
        public IReadOnlyList<GitHubCodespaceRemote> Codespaces { get; init; } = [];
        public List<(string Token, string Name)> DeleteCalls { get; } = [];

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile("octocat", "Octo Cat", "Free"));

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Codespaces);

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            DeleteCalls.Add((token, codespaceName));
            return Task.CompletedTask;
        }

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(StorageLimitedUsage());
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public string Protect(string value) => value;

        public string Unprotect(string value) => value;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class NoopOperationalEventSink : IOperationalEventSink
    {
        public Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
