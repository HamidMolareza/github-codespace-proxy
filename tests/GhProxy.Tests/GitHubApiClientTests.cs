using System.Net;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GhProxy.Tests;

public sealed class GitHubApiClientTests
{
    [Fact]
    public async Task GetAuthenticatedUserAsync_ReturnsLoginNameAndPlan()
    {
        const string responseJson = """
        {
          "login": "octocat",
          "name": "Octo Cat",
          "plan": { "name": "Pro" }
        }
        """;
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.OK, responseJson));

        var profile = await client.GetAuthenticatedUserAsync("token", CancellationToken.None);

        Assert.Equal("octocat", profile.Login);
        Assert.Equal("Octo Cat", profile.Name);
        Assert.Equal("Pro", profile.PlanName);
    }

    [Fact]
    public async Task GetCodespacesUsageAsync_GroupsComputeAndStorageUsage()
    {
        const string responseJson = """
        {
          "timePeriod": { "year": 2026, "month": 5 },
          "usageItems": [
            { "product": "Codespaces", "sku": "codespaces_compute_d2", "grossQuantity": 20.3808, "discountQuantity": 20.3808, "netQuantity": 0, "unitType": "hours", "netAmount": 0 },
            { "product": "Codespaces", "sku": "codespaces_storage", "grossQuantity": 0.0068, "discountQuantity": 0.0068, "netQuantity": 0, "unitType": "gigabyte-hours", "netAmount": 0 },
            { "product": "Actions", "sku": "actions_linux", "quantity": 99, "unitType": "minutes", "netAmount": 0 }
          ]
        }
        """;
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.OK, responseJson));

        var usage = await client.GetCodespacesUsageAsync("token", "octocat", CancellationToken.None);

        Assert.Equal(GitHubAccountQuotaState.Healthy, usage.State);
        Assert.Equal(20.3876m, usage.Quantity);
        Assert.Equal("hours", usage.UnitType);
        Assert.Equal(2026, usage.BillingPeriodYear);
        Assert.Equal(5, usage.BillingPeriodMonth);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), usage.ResetAt);
        var compute = Assert.Single(usage.Quotas, x => x.Name == "Compute");
        Assert.Equal(40.7616m, compute.Used);
        Assert.Equal("included units", compute.Unit);
        var storage = Assert.Single(usage.Quotas, x => x.Name == "Storage");
        Assert.Equal(Math.Round(0.0068m / (31 * 24), 12), Math.Round(storage.Used, 12));
        Assert.Equal("GB-month", storage.Unit);
    }

    [Fact]
    public async Task GetCodespacesUsageAsync_ReturnsUnavailableWhenBillingEndpointIsForbidden()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.Forbidden, "{}"));

        var usage = await client.GetCodespacesUsageAsync("token", "octocat", CancellationToken.None);

        Assert.Equal(GitHubAccountQuotaState.Unavailable, usage.State);
        Assert.Empty(usage.Quotas);
    }

    [Fact]
    public async Task ExportCodespaceAsync_IncludesGitHubValidationMessage()
    {
        const string responseJson = """
        {
          "message": "Validation Failed",
          "errors": [
            { "resource": "Codespace", "field": "name", "code": "invalid", "message": "export is already running" }
          ]
        }
        """;
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.UnprocessableEntity, responseJson));

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.ExportCodespaceAsync("token", "space", CancellationToken.None));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("Validation Failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export is already running", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestCodespaceExportAsync_ReturnsLatestExport()
    {
        const string responseJson = """
        {
          "id": "export-id",
          "state": "succeeded",
          "export_url": "https://api.github.test/export",
          "html_url": "https://github.test/export",
          "completed_at": "2026-05-21T10:20:30Z"
        }
        """;
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.OK, responseJson));

        var export = await client.GetLatestCodespaceExportAsync("token", "space", CancellationToken.None);

        Assert.NotNull(export);
        Assert.Equal("export-id", export.Id);
        Assert.Equal("succeeded", export.State);
        Assert.Equal(DateTimeOffset.Parse("2026-05-21T10:20:30Z"), export.CompletedAt);
    }

    private static GitHubApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        return new GitHubApiClient(
            httpClient,
            Options.Create(new GitHubOptions()),
            new NoopOperationalEventSink(),
            new SensitiveDataRedactor(),
            NullLogger<GitHubApiClient>.Instance);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(responseBody)
            });
    }

    private sealed class NoopOperationalEventSink : IOperationalEventSink
    {
        public Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
