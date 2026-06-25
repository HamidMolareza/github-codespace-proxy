using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Tests;

public sealed class CodespaceProxyAutomationServiceTests
{
    [Fact]
    public async Task SelectAsync_ReusesActiveCodespaceAcrossAccountsBeforeCreating()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var low = CreateAccount("Low", "low", now);
            var high = CreateAccount("High", "high", now);
            db.GitHubAccounts.AddRange(low, high);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["low-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.UsageByToken["high-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing", []);
            github.CodespacesByToken["high-token"] =
            [
                new GitHubCodespaceRemote("existing-active", "Available", "high/proxy2", "2-core", "UsEast", null, "high", now.AddHours(-3), now.AddHours(-2), now.AddHours(-1))
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(high.Id, result.Selection.AccountId);
            Assert.Equal("existing-active", result.Selection.CodespaceName);
            Assert.Empty(github.CreateCalls);
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
    public async Task SelectAsync_ReusesStoppedCodespaceBeforeCreating()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var low = CreateAccount("Low", "low", now);
            var high = CreateAccount("High", "high", now);
            db.GitHubAccounts.AddRange(low, high);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["low-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.UsageByToken["high-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing", []);
            github.CodespacesByToken["high-token"] =
            [
                new GitHubCodespaceRemote("existing-stopped", "Shutdown", "high/proxy2", "2-core", "UsEast", null, "high", now.AddHours(-3), now.AddHours(-2), now.AddHours(-1))
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(high.Id, result.Selection.AccountId);
            Assert.Equal("existing-stopped", result.Selection.CodespaceName);
            Assert.Empty(github.CreateCalls);
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
    public async Task SelectAsync_CreatesOnlyWhenNoReusableCodespaceExists()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var low = CreateAccount("Low", "low", now);
            var high = CreateAccount("High", "high", now);
            db.GitHubAccounts.AddRange(low, high);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["low-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.UsageByToken["high-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing", []);
            github.CodespacesByToken["high-token"] =
            [
                new GitHubCodespaceRemote("other-repo", "Available", "high/other", "2-core", "UsEast", null, "high", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(low.Id, result.Selection.AccountId);
            Assert.Equal("created-1", result.Selection.CodespaceName);
            Assert.Single(github.CreateCalls);
            Assert.Equal("low-token", github.CreateCalls[0].Token);
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
    public async Task SelectAsync_PrefersActiveExistingOverStoppedExisting()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var active = CreateAccount("Active", "active", now);
            var stopped = CreateAccount("Stopped", "stopped", now);
            db.GitHubAccounts.AddRange(active, stopped);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["active-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing", []);
            github.UsageByToken["stopped-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.CodespacesByToken["active-token"] =
            [
                new GitHubCodespaceRemote("active-space", "Available", "active/proxy2", "2-core", "UsEast", null, "active", now.AddDays(-10), now.AddDays(-9), now.AddDays(-8))
            ];
            github.CodespacesByToken["stopped-token"] =
            [
                new GitHubCodespaceRemote("stopped-space", "Shutdown", "stopped/proxy2", "2-core", "UsEast", null, "stopped", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(active.Id, result.Selection.AccountId);
            Assert.Equal("active-space", result.Selection.CodespaceName);
            Assert.Empty(github.CreateCalls);
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
    public async Task SelectAsync_StopsExtraActiveProxyCodespacesOnly()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var low = CreateAccount("Low", "low", now);
            var high = CreateAccount("High", "high", now);
            var unrelated = CreateAccount("Unrelated", "unrelated", now);
            db.GitHubAccounts.AddRange(low, high, unrelated);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["low-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.UsageByToken["high-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing", []);
            github.UsageByToken["unrelated-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 20, "hours", 0, "billing", []);
            github.CodespacesByToken["low-token"] =
            [
                new GitHubCodespaceRemote("selected", "Available", "low/proxy2", "2-core", "UsEast", null, "low", now, now, now)
            ];
            github.CodespacesByToken["high-token"] =
            [
                new GitHubCodespaceRemote("extra", "Available", "high/proxy2", "2-core", "UsEast", null, "high", now.AddHours(-2), now.AddHours(-1), now.AddMinutes(-30))
            ];
            github.CodespacesByToken["unrelated-token"] =
            [
                new GitHubCodespaceRemote("manual", "Available", "unrelated/manual-repo", "2-core", "UsEast", null, "unrelated", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(low.Id, result.Selection.AccountId);
            Assert.Equal("selected", result.Selection.CodespaceName);
            Assert.Contains(("high-token", "extra"), github.StopCalls);
            Assert.DoesNotContain(("unrelated-token", "manual"), github.StopCalls);
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
    public async Task SelectAsync_DeletesStorageLimitedStoppedProxyCodespaceBeforeSelection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var limited = CreateAccount("Limited", "limited", now);
            var healthy = CreateAccount("Healthy", "healthy", now);
            db.GitHubAccounts.AddRange(limited, healthy);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["limited-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Limited,
                "storage limited",
                null,
                null,
                null,
                "billing",
                [new GitHubUsageQuotaSummaryResponse("Storage", 15, 15, 0, 100, "GB-month")]);
            github.UsageByToken["healthy-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.CodespacesByToken["limited-token"] =
            [
                new GitHubCodespaceRemote("storage-cost", "Shutdown", "limited/proxy2", "2-core", "UsEast", null, "limited", now.AddHours(-3), now.AddHours(-2), now.AddHours(-1))
            ];
            github.CodespacesByToken["healthy-token"] =
            [
                new GitHubCodespaceRemote("selected", "Available", "healthy/proxy2", "2-core", "UsEast", null, "healthy", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(healthy.Id, result.Selection.AccountId);
            Assert.Equal("selected", result.Selection.CodespaceName);
            Assert.Contains(("limited-token", "storage-cost"), github.DeleteCalls);
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
    public async Task SelectAsync_SkipsLimitedActiveCodespaceAndUsesHealthyProxyRepository()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var limited = CreateAccount("Limited", "limited", now);
            var healthy = CreateAccount("Healthy", "healthy", now);
            db.GitHubAccounts.AddRange(limited, healthy);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["limited-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Limited, "limited", null, null, null, "billing", []);
            github.UsageByToken["healthy-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.CodespacesByToken["limited-token"] =
            [
                new GitHubCodespaceRemote("limited-active", "Available", "limited/proxy2", "2-core", "UsEast", null, "limited", now, now, now)
            ];
            github.CodespacesByToken["healthy-token"] =
            [
                new GitHubCodespaceRemote("healthy-active", "Available", "healthy/proxy", "2-core", "UsEast", null, "healthy", now.AddHours(-3), now.AddHours(-2), now.AddHours(-1))
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(healthy.Id, result.Selection.AccountId);
            Assert.Equal("healthy-active", result.Selection.CodespaceName);
            Assert.Equal("healthy/proxy", result.Selection.RepositoryFullName);
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
    public async Task SelectAsync_ReusesAccountOwnedProxyPrefixRepositoriesOnly()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var proxy = CreateAccount("Proxy", "proxyuser", now);
            var other = CreateAccount("Other", "other", now);
            db.GitHubAccounts.AddRange(proxy, other);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["proxyuser-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 3, "hours", 0, "billing", []);
            github.UsageByToken["other-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing", []);
            github.CodespacesByToken["proxyuser-token"] =
            [
                new GitHubCodespaceRemote("proxy1-active", "Available", "proxyuser/proxy1", "2-core", "UsEast", null, "proxyuser", now.AddHours(-3), now.AddHours(-2), now.AddHours(-1)),
                new GitHubCodespaceRemote("non-owned", "Available", "someone/proxy2", "2-core", "UsEast", null, "proxyuser", now, now, now),
                new GitHubCodespaceRemote("not-proxy", "Available", "proxyuser/workspace", "2-core", "UsEast", null, "proxyuser", now, now, now)
            ];
            github.CodespacesByToken["other-token"] =
            [
                new GitHubCodespaceRemote("other-proxy2", "Shutdown", "other/proxy2", "2-core", "UsEast", null, "other", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(proxy.Id, result.Selection.AccountId);
            Assert.Equal("proxy1-active", result.Selection.CodespaceName);
            Assert.Equal("proxyuser/proxy1", result.Selection.RepositoryFullName);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static GitHubAccount CreateAccount(string displayName, string username, DateTimeOffset now) =>
        new()
        {
            DisplayName = displayName,
            Username = username,
            ProtectedPersonalAccessToken = $"{username}-token",
            ValidationStatus = GitHubAccountValidationStatus.Valid,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static CodespaceProxyAutomationService CreateService(AppDbContext db, IGitHubApiClient github)
    {
        var clock = new TestClock();
        var events = new NoopOperationalEventSink();
        var codespaces = new GitHubCodespaceService(db, github, new PassThroughSecretProtector(), clock, new AuditService(db, clock, events), events);
        var storageCleanup = new CodespaceStorageCleanupService(
            db,
            codespaces,
            events,
            Options.Create(new GitHubOptions()));
        return new CodespaceProxyAutomationService(db, codespaces, storageCleanup, github, new PassThroughSecretProtector(), events, Options.Create(new LocalProxyOptions()));
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
        public Dictionary<string, GitHubUsageResponse> UsageByToken { get; } = [];
        public Dictionary<string, IReadOnlyList<GitHubCodespaceRemote>> CodespacesByToken { get; } = [];
        public List<(string Token, string Name)> StopCalls { get; } = [];
        public List<(string Token, string Name)> DeleteCalls { get; } = [];
        public List<(string Token, CreateCodespaceRequest Request)> CreateCalls { get; } = [];

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile(token.Replace("-token", "", StringComparison.Ordinal), null, "Free"));

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(CodespacesByToken.GetValueOrDefault(token) ?? []);

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken)
        {
            CreateCalls.Add((token, request));
            var now = DateTimeOffset.UtcNow;
            var created = new GitHubCodespaceRemote(
                $"created-{CreateCalls.Count}",
                "Provisioning",
                $"{request.RepositoryOwner}/{request.RepositoryName}",
                request.Machine,
                request.Geo,
                null,
                request.DisplayName,
                now,
                now,
                now);
            CodespacesByToken[token] = [.. (CodespacesByToken.GetValueOrDefault(token) ?? []), created];
            return Task.FromResult(created);
        }

        public Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            StopCalls.Add((token, codespaceName));
            var existing = CodespacesByToken[token].Single(x => x.Name == codespaceName);
            var stopped = existing with { State = "Shutdown" };
            CodespacesByToken[token] = CodespacesByToken[token]
                .Select(x => x.Name == codespaceName ? stopped : x)
                .ToList();
            return Task.FromResult(stopped);
        }

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            DeleteCalls.Add((token, codespaceName));
            CodespacesByToken[token] = (CodespacesByToken.GetValueOrDefault(token) ?? [])
                .Where(x => !string.Equals(x.Name, codespaceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.CompletedTask;
        }

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(UsageByToken.GetValueOrDefault(token) ?? new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", null, null, null, "billing", []));
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
