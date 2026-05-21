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
    public async Task SelectAsync_ChoosesLowestUsageAccountAndStopsExtraActiveCodespaces()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var low = new GitHubAccount
            {
                DisplayName = "Low",
                Username = "low",
                ProtectedPersonalAccessToken = "low-token",
                ValidationStatus = GitHubAccountValidationStatus.Valid,
                CreatedAt = now,
                UpdatedAt = now
            };
            var high = new GitHubAccount
            {
                DisplayName = "High",
                Username = "high",
                ProtectedPersonalAccessToken = "high-token",
                ValidationStatus = GitHubAccountValidationStatus.Valid,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.GitHubAccounts.AddRange(low, high);
            await db.SaveChangesAsync();

            var github = new FakeGitHubApiClient();
            github.UsageByToken["low-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 1, "hours", 0, "billing");
            github.UsageByToken["high-token"] = new GitHubUsageResponse(GitHubAccountQuotaState.Healthy, "ok", 10, "hours", 0, "billing");
            github.CodespacesByToken["low-token"] =
            [
                new GitHubCodespaceRemote("selected", "Available", "low/proxy2", "2-core", "UsEast", null, "low", now, now, now)
            ];
            github.CodespacesByToken["high-token"] =
            [
                new GitHubCodespaceRemote("extra", "Available", "high/proxy2", "2-core", "UsEast", null, "high", now, now, now)
            ];

            var service = CreateService(db, github);

            var result = await service.SelectAsync(CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Selection);
            Assert.Equal(low.Id, result.Selection.AccountId);
            Assert.Equal("selected", result.Selection.CodespaceName);
            Assert.Contains(("high-token", "extra"), github.StopCalls);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static CodespaceProxyAutomationService CreateService(AppDbContext db, IGitHubApiClient github)
    {
        var clock = new TestClock();
        var events = new NoopOperationalEventSink();
        var codespaces = new GitHubCodespaceService(db, github, new PassThroughSecretProtector(), clock, new AuditService(db, clock, events), events);
        return new CodespaceProxyAutomationService(db, codespaces, github, new PassThroughSecretProtector(), events, Options.Create(new LocalProxyOptions()));
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

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubUserProfile(token.Replace("-token", "", StringComparison.Ordinal)));

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitHubCodespaceRemote>> ListCodespacesAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(CodespacesByToken.GetValueOrDefault(token) ?? []);

        public Task<GitHubCodespaceRemote> CreateCodespaceAsync(string token, CreateCodespaceRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task DeleteCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(UsageByToken[token]);
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
