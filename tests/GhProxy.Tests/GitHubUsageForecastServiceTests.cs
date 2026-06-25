using System.Net;
using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class GitHubUsageForecastServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsAggregateForecastCappedByResetDate()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var account = CreateAccount("Primary", "octocat", "token", "Free", now);
            var profile = CreateProfile(now);
            db.GitHubAccounts.Add(account);
            db.LocalProxyProfiles.Add(profile);
            db.CodespaceSnapshots.Add(new CodespaceSnapshot
            {
                AccountId = account.Id,
                Name = "space",
                State = "Available",
                MachineDisplayName = "2 cores, 8 GB RAM, 32 GB storage",
                LastSyncedAt = now
            });
            for (var index = 0; index < 30; index += 1)
            {
                var start = now.AddDays(-30 + index).AddHours(1);
                db.LocalProxySessions.Add(CreateSession(profile.Id, account.Id, "space", start, start.AddHours(1)));
            }

            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            github.UsageByToken["token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                "ok",
                60,
                "core hours",
                0,
                "billing",
                [new GitHubUsageQuotaSummaryResponse("Compute", 60, null, null, null, "core hours")],
                2026,
                6,
                GitHubUsageForecastService.NextResetAt(2026, 6));
            var service = CreateForecastService(db, github, now);

            var forecast = await service.GetAsync(CancellationToken.None);

            Assert.Equal("Healthy", forecast.Status);
            Assert.Equal(1, forecast.IncludedAccountCount);
            Assert.Equal(1, forecast.UsableAccountCount);
            Assert.Equal(0, forecast.LimitedAccountCount);
            Assert.Equal(19, forecast.DaysUntilReset);
            Assert.Equal(120m, forecast.TotalComputeLimit);
            Assert.Equal(60m, forecast.TotalComputeRemaining);
            Assert.Equal(2m, forecast.Average7DayComputeUsage);
            Assert.Equal(2m, forecast.EstimatedDailyComputeUsage);
            Assert.Equal(30m, forecast.EstimatedQuotaDays);
            Assert.Equal(19, forecast.EstimatedUsableDays);
            Assert.Empty(forecast.Warnings);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_IncludesKnownPlanAccountsWithEmptyUsageRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            db.GitHubAccounts.Add(CreateAccount("Used", "used", "used-token", "Free", now));
            db.GitHubAccounts.Add(CreateAccount("Empty", "empty", "empty-token", "Free", now));
            db.GitHubAccounts.Add(CreateAccount("Limited", "limited", "limited-token", "Free", now));
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            github.UsageByToken["used-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                "ok",
                35,
                "core hours",
                0,
                "billing",
                [new GitHubUsageQuotaSummaryResponse("Compute", 70, null, null, null, "core hours")],
                2026,
                6,
                GitHubUsageForecastService.NextResetAt(2026, 6));
            github.UsageByToken["empty-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                "Usage endpoint is reachable, but no Codespaces items were returned.",
                null,
                null,
                null,
                "billing",
                [],
                2026,
                6,
                GitHubUsageForecastService.NextResetAt(2026, 6));
            github.UsageByToken["limited-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                "ok",
                60,
                "core hours",
                0,
                "billing",
                [new GitHubUsageQuotaSummaryResponse("Compute", 120, null, null, null, "core hours")],
                2026,
                6,
                GitHubUsageForecastService.NextResetAt(2026, 6));
            var service = CreateForecastService(db, github, now);

            var forecast = await service.GetAsync(CancellationToken.None);

            Assert.Equal(3, forecast.IncludedAccountCount);
            Assert.Equal(2, forecast.UsableAccountCount);
            Assert.Equal(1, forecast.LimitedAccountCount);
            Assert.Equal(360m, forecast.TotalComputeLimit);
            Assert.Equal(170m, forecast.TotalComputeRemaining);
            Assert.DoesNotContain(forecast.Warnings, warning => warning.Contains("No compute usage was returned", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public void EstimateDailyComputeUsage_PrioritizesRecentDaysAndVolatility()
    {
        var daily = Enumerable.Repeat(1m, 23).Concat(Enumerable.Repeat(5m, 7)).ToList();

        var estimate = GitHubUsageForecastService.EstimateDailyComputeUsage(daily);

        Assert.InRange(estimate, 4.18m, 4.19m);
    }

    [Fact]
    public async Task GetAsync_WarnsWhenAccountsCannotBeIncluded()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            db.GitHubAccounts.Add(CreateAccount("Unknown", "unknown", "unknown-token", "Unknown", now));
            db.GitHubAccounts.Add(CreateAccount("Forbidden", "forbidden", "forbidden-token", "Free", now));
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient();
            github.UsageByToken["unknown-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Healthy,
                "ok",
                1,
                "core hours",
                0,
                "billing",
                [new GitHubUsageQuotaSummaryResponse("Compute", 1, null, null, null, "core hours")],
                2026,
                6,
                GitHubUsageForecastService.NextResetAt(2026, 6));
            github.UsageByToken["forbidden-token"] = new GitHubUsageResponse(
                GitHubAccountQuotaState.Unavailable,
                "unavailable",
                null,
                null,
                null,
                "billing",
                []);
            var service = CreateForecastService(db, github, now);

            var forecast = await service.GetAsync(CancellationToken.None);

            Assert.Equal("Unavailable", forecast.Status);
            Assert.Equal(0, forecast.IncludedAccountCount);
            Assert.Equal(1, forecast.UnavailableAccountCount);
            Assert.Contains(forecast.Warnings, warning => warning.Contains("unknown", StringComparison.OrdinalIgnoreCase) && warning.Contains("known Free/Pro", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(forecast.Warnings, warning => warning.Contains("forbidden", StringComparison.OrdinalIgnoreCase) && warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static GitHubUsageForecastService CreateForecastService(AppDbContext db, IGitHubApiClient github, DateTimeOffset now)
    {
        var clock = new TestClock(now);
        var events = new NoopOperationalEventSink();
        var codespaces = new GitHubCodespaceService(db, github, new PassThroughSecretProtector(), clock, new AuditService(db, clock, events), events);
        return new GitHubUsageForecastService(db, codespaces, clock);
    }

    private static AppDbContext CreateDb(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new AppDbContext(options);
    }

    private static GitHubAccount CreateAccount(string displayName, string username, string token, string plan, DateTimeOffset now) =>
        new()
        {
            DisplayName = displayName,
            Username = username,
            ProtectedPersonalAccessToken = token,
            Plan = plan,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static LocalProxyProfile CreateProfile(DateTimeOffset now) =>
        new()
        {
            Name = "Default",
            BindHost = "127.0.0.1",
            LocalPort = 8910,
            SocksPort = 8910,
            IdleShutdownMinutes = 30,
            Status = LocalProxyProfileStatus.Stopped,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static LocalProxySession CreateSession(Guid profileId, Guid accountId, string codespaceName, DateTimeOffset start, DateTimeOffset end) =>
        new()
        {
            ProfileId = profileId,
            Status = LocalProxySessionStatus.Stopped,
            StartedAt = start,
            LastActivityAt = end,
            StoppedAt = end,
            AccountId = accountId,
            CodespaceName = codespaceName
        };

    private static void DeleteDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private sealed class FakeGitHubApiClient : IGitHubApiClient
    {
        public Dictionary<string, GitHubUsageResponse> UsageByToken { get; } = [];

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile("octocat", "Octo Cat", "Free"));

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GitHubCodespaceRemote>>([]);

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StartCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceRemote> StopCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubCodespaceExportRemote?>(null);

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(UsageByToken.GetValueOrDefault(token) ?? throw new GitHubApiException(HttpStatusCode.NotFound, "not found"));
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public string Protect(string value) => value;

        public string Unprotect(string value) => value;
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class NoopOperationalEventSink : IOperationalEventSink
    {
        public Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
