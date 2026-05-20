using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace GhProxy.Tests;

public sealed class GitHubCodespaceServiceTests
{
    [Fact]
    public async Task SyncAsync_UpsertsRemoteCodespacesAndRemovesStaleRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = new GitHubAccount
            {
                DisplayName = "Primary",
                Username = "octocat",
                ProtectedPersonalAccessToken = "token",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            db.CodespaceSnapshots.Add(new CodespaceSnapshot
            {
                AccountId = account.Id,
                Name = "stale",
                State = "Available",
                LastSyncedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient
            {
                Codespaces =
                [
                    new GitHubCodespaceRemote(
                        "fresh",
                        "Available",
                        "octocat/hello",
                        "2-core",
                        "UsEast",
                        "https://github.com/codespaces/fresh",
                        "octocat",
                        DateTimeOffset.UtcNow.AddHours(-2),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddMinutes(-10))
                ]
            };
            var service = CreateService(db, github);

            var snapshots = await service.SyncAsync(account.Id, CancellationToken.None);

            var snapshot = Assert.Single(snapshots);
            Assert.Equal("fresh", snapshot.Name);
            Assert.Equal("octocat/hello", snapshot.RepositoryFullName);
            Assert.False(await db.CodespaceSnapshots.AnyAsync(x => x.Name == "stale"));
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
    public async Task CreateAsync_BlocksLimitedAccounts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = new GitHubAccount
            {
                DisplayName = "Limited",
                Username = "octocat",
                ProtectedPersonalAccessToken = "token",
                QuotaState = GitHubAccountQuotaState.Limited,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            var service = CreateService(db, github);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
                account.Id,
                new CreateCodespaceRequest("octocat", "hello", null, "UsEast", null, null, 30),
                CancellationToken.None));
            Assert.Equal(0, github.CreateCalls);
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
    public async Task RefreshCodespaceAsync_SyncsAndReturnsRequestedCodespace()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = new GitHubAccount
            {
                DisplayName = "Primary",
                Username = "octocat",
                ProtectedPersonalAccessToken = "token",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient
            {
                Codespaces =
                [
                    new GitHubCodespaceRemote(
                        "fresh",
                        "Available",
                        "octocat/hello",
                        "2-core",
                        "UsEast",
                        "https://github.com/codespaces/fresh",
                        "octocat",
                        DateTimeOffset.UtcNow.AddHours(-2),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddMinutes(-10))
                ]
            };
            var service = CreateService(db, github);

            var snapshot = await service.RefreshCodespaceAsync(account.Id, "fresh", CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("Available", snapshot.State);
            Assert.Equal("octocat/hello", snapshot.RepositoryFullName);
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
    public async Task StartAsync_TreatsGitHubConflictAsRefreshableLifecycleState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = new GitHubAccount
            {
                DisplayName = "Primary",
                Username = "octocat",
                ProtectedPersonalAccessToken = "token",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient
            {
                ThrowStartConflict = true,
                Codespaces =
                [
                    new GitHubCodespaceRemote(
                        "fresh",
                        "Starting",
                        "octocat/hello",
                        "2-core",
                        "UsEast",
                        "https://github.com/codespaces/fresh",
                        "octocat",
                        DateTimeOffset.UtcNow.AddHours(-2),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddMinutes(-10))
                ]
            };
            var service = CreateService(db, github);

            var snapshot = await service.StartAsync(account.Id, "fresh", CancellationToken.None);

            Assert.Equal("fresh", snapshot.Name);
            Assert.Equal("Starting", snapshot.State);
            Assert.Equal(1, github.StartCalls);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static GitHubCodespaceService CreateService(AppDbContext db, IGitHubApiClient github)
    {
        var clock = new TestClock();
        var events = new NoopOperationalEventSink();
        return new GitHubCodespaceService(db, github, new PassThroughSecretProtector(), clock, new AuditService(db, clock, events), events);
    }

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
        public int CreateCalls { get; private set; }
        public int StartCalls { get; private set; }
        public bool ThrowStartConflict { get; init; }

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile("octocat"));

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(Codespaces);

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(Codespaces.First());
        }

        public Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            StartCalls++;
            if (ThrowStartConflict)
            {
                throw new GitHubApiException(HttpStatusCode.Conflict, "GitHub API returned 409 Conflict.", "{}");
            }

            return Task.FromResult(Codespaces.First(x => x.Name == codespaceName));
        }

        public Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.FromResult(Codespaces.First(x => x.Name == codespaceName));

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubCodespaceExportRemote("export-id", "pending", "https://api.github.com/export", "https://github.com/export", null));

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "minutes", 0, "https://github.com/settings/billing/usage"));
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
