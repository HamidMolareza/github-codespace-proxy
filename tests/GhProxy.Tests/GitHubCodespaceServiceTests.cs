using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Endpoints;
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
    public async Task CreateAccountAsync_UsesTokenMetadataWhenOptionalFieldsAreMissing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var github = new FakeGitHubApiClient
            {
                ProfilesByToken =
                {
                    ["token"] = new GitHubUserProfile("hamid", "Hamid Molareza", "Pro")
                }
            };
            var service = CreateService(db, github);

            var account = await service.CreateAccountAsync(new GitHubAccountRequest(null, null, "token", "Unknown"), CancellationToken.None);

            Assert.Equal("hamid", account.Username);
            Assert.Equal("Hamid Molareza", account.DisplayName);
            Assert.Equal("Pro", account.Plan);
            Assert.Equal(GitHubAccountValidationStatus.Valid, account.ValidationStatus);
            Assert.NotNull(account.LastValidatedAt);
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
    public async Task CheckAllStatusesAsync_ContinuesWhenOneAccountFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var good = new GitHubAccount
            {
                DisplayName = "Good",
                Username = "good",
                ProtectedPersonalAccessToken = "good-token",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var bad = new GitHubAccount
            {
                DisplayName = "Bad",
                Username = "bad",
                ProtectedPersonalAccessToken = "bad-token",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.AddRange(good, bad);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient
            {
                ProfilesByToken =
                {
                    ["good-token"] = new GitHubUserProfile("good", "Good Account", "Free")
                },
                InvalidTokens = { "bad-token" },
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

            var results = await service.CheckAllStatusesAsync(CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, result => result.AccountId == good.Id && result.Succeeded);
            Assert.Contains(results, result => result.AccountId == bad.Id && !result.Succeeded);
            Assert.NotNull(good.LastSyncedAt);
            Assert.Equal(GitHubAccountValidationStatus.Invalid, bad.ValidationStatus);
            Assert.Single(await db.CodespaceSnapshots.Where(x => x.AccountId == good.Id).ToListAsync());
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
    public void ToResponse_CountsActiveAndTotalCodespaces()
    {
        var account = new GitHubAccount
        {
            DisplayName = "Primary",
            Username = "octocat",
            ProtectedPersonalAccessToken = "token",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Codespaces =
            [
                new CodespaceSnapshot { Name = "running", State = "Available" },
                new CodespaceSnapshot { Name = "starting", State = "Provisioning" },
                new CodespaceSnapshot { Name = "stopped", State = "Shutdown" }
            ]
        };

        var response = GitHubEndpoints.ToResponse(account);

        Assert.Equal(2, response.ActiveCodespaceCount);
        Assert.Equal(3, response.TotalCodespaceCount);
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

    [Fact]
    public async Task GetUsageAsync_AppliesFreePlanQuotaAndWarningState()
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
                Plan = "Free",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient
            {
                Usage = new GitHubUsageResponse(
                    GitHubAccountQuotaState.Healthy,
                    "ok",
                    110,
                    "core hours",
                    0,
                    "billing",
                    [
                        new GitHubUsageQuotaSummaryResponse("Compute", 110, null, null, null, "core hours"),
                        new GitHubUsageQuotaSummaryResponse("Storage", 10, null, null, null, "GB-month")
                    ])
            };
            var service = CreateService(db, github);

            var usage = await service.GetUsageAsync(account.Id, CancellationToken.None);

            Assert.Equal(GitHubAccountQuotaState.Warning, usage.State);
            Assert.Equal(GitHubAccountQuotaState.Warning, account.QuotaState);
            var compute = Assert.Single(usage.Quotas, x => x.Name == "Compute");
            Assert.Equal(120m, compute.Limit);
            Assert.Equal(10m, compute.Remaining);
            Assert.Equal(91.7m, compute.PercentUsed);
            var storage = Assert.Single(usage.Quotas, x => x.Name == "Storage");
            Assert.Equal(15m, storage.Limit);
            Assert.Equal(5m, storage.Remaining);
            Assert.Equal(66.7m, storage.PercentUsed);
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
    public async Task GetUsageAsync_MarksProPlanLimitedWhenQuotaIsExhausted()
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
                Plan = "Pro",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.GitHubAccounts.Add(account);
            await db.SaveChangesAsync();
            var github = new FakeGitHubApiClient
            {
                Usage = new GitHubUsageResponse(
                    GitHubAccountQuotaState.Healthy,
                    "ok",
                    181,
                    "core hours",
                    0,
                    "billing",
                    [new GitHubUsageQuotaSummaryResponse("Compute", 181, null, null, null, "core hours")])
            };
            var service = CreateService(db, github);

            var usage = await service.GetUsageAsync(account.Id, CancellationToken.None);

            Assert.Equal(GitHubAccountQuotaState.Limited, usage.State);
            var compute = Assert.Single(usage.Quotas);
            Assert.Equal(180m, compute.Limit);
            Assert.Equal(0m, compute.Remaining);
            Assert.Equal(100.6m, compute.PercentUsed);
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
    public async Task ExportAsync_ReturnsRejectedLatestFailureDetails()
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
                ThrowExportValidation = true,
                LatestExport = new GitHubCodespaceExportRemote("latest", "failed", "https://api.github.com/export/latest", null, DateTimeOffset.UtcNow)
            };
            var service = CreateService(db, github);

            var result = await service.ExportAsync(account.Id, "fresh", CancellationToken.None);

            Assert.False(result.AcceptedNewExport);
            Assert.Equal("failed", result.Export.State);
            Assert.Contains("Validation Failed", result.RejectionMessage, StringComparison.OrdinalIgnoreCase);
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
        public GitHubUsageResponse Usage { get; init; } = new(
            GitHubAccountQuotaState.Healthy,
            "ok",
            10,
            "minutes",
            0,
            "https://github.com/settings/billing/usage",
            []);
        public GitHubCodespaceExportRemote? LatestExport { get; init; } = new("latest-export-id", "succeeded", "https://api.github.com/export/latest", "https://github.com/export/latest", DateTimeOffset.UtcNow);
        public string AuthenticatedLogin { get; init; } = "octocat";
        public string? AuthenticatedName { get; init; } = "Octo Cat";
        public string? AuthenticatedPlan { get; init; } = "Free";
        public Dictionary<string, GitHubUserProfile> ProfilesByToken { get; init; } = [];
        public HashSet<string> InvalidTokens { get; init; } = [];
        public int CreateCalls { get; private set; }
        public int StartCalls { get; private set; }
        public bool ThrowStartConflict { get; init; }
        public bool ThrowExportValidation { get; init; }

        public Task<GitHubUserProfile> GetAuthenticatedUserAsync(string token, CancellationToken cancellationToken)
        {
            if (InvalidTokens.Contains(token))
            {
                throw new GitHubApiException(HttpStatusCode.Unauthorized, "Bad credentials.", "{}");
            }

            return Task.FromResult(ProfilesByToken.TryGetValue(token, out var profile)
                ? profile
                : new GitHubUserProfile(AuthenticatedLogin, AuthenticatedName, AuthenticatedPlan));
        }

        public Task<bool> RepositoryExistsAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ForkRepositoryAsync(string token, string owner, string repository, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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

        public Task<GitHubCodespaceExportRemote> ExportCodespaceAsync(string token, string codespaceName, CancellationToken cancellationToken)
        {
            if (ThrowExportValidation)
            {
                throw new GitHubApiException(HttpStatusCode.UnprocessableEntity, "GitHub API returned 422 Unprocessable Entity. Validation Failed.", "{}");
            }

            return Task.FromResult(new GitHubCodespaceExportRemote("export-id", "pending", "https://api.github.com/export", "https://github.com/export", null));
        }

        public Task<GitHubCodespaceExportRemote?> GetLatestCodespaceExportAsync(string token, string codespaceName, CancellationToken cancellationToken) =>
            Task.FromResult(LatestExport);

        public Task<GitHubUsageResponse> GetCodespacesUsageAsync(string token, string username, CancellationToken cancellationToken) =>
            Task.FromResult(Usage);
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
