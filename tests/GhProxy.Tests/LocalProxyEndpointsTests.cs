using GhProxy.Api.Endpoints;
using GhProxy.Api.Domain;

namespace GhProxy.Tests;

public sealed class LocalProxyEndpointsTests
{
    [Fact]
    public void GetLatestRequestAt_ReturnsNewestTimestampAcrossStatusSources()
    {
        var gatewayRequestAt = new DateTimeOffset(2026, 5, 22, 6, 23, 16, TimeSpan.Zero);
        var activeSessionRequestAt = new DateTimeOffset(2026, 5, 22, 6, 31, 8, TimeSpan.Zero);
        var savedSessionRequestAt = new DateTimeOffset(2026, 5, 22, 6, 27, 23, TimeSpan.Zero);

        var latestRequestAt = LocalProxyEndpoints.GetLatestRequestAt(
            gatewayRequestAt,
            activeSessionRequestAt,
            savedSessionRequestAt);

        Assert.Equal(activeSessionRequestAt, latestRequestAt);
    }

    [Fact]
    public void GetLatestRequestAt_IgnoresMissingStatusSources()
    {
        var savedSessionRequestAt = new DateTimeOffset(2026, 5, 22, 6, 27, 23, TimeSpan.Zero);

        var latestRequestAt = LocalProxyEndpoints.GetLatestRequestAt(null, null, savedSessionRequestAt);

        Assert.Equal(savedSessionRequestAt, latestRequestAt);
    }

    [Fact]
    public void ToLatestRequestResponses_ReturnsNewestRequestsWithLimit()
    {
        var start = new DateTimeOffset(2026, 5, 22, 6, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 25)
            .Select(index => new LocalProxyGatewayRequest
            {
                Id = Guid.NewGuid(),
                ObservedAt = start.AddMinutes(index),
                Protocol = "HTTP",
                TargetHost = $"host-{index}.test",
                TargetPort = 443,
                Outcome = "Forwarded",
                CodespaceName = "space",
                DurationMs = index
            })
            .ToList();

        var latest = LocalProxyEndpoints.ToLatestRequestResponses(requests);

        Assert.Equal(20, latest.Count);
        Assert.Equal("host-24.test", latest[0].TargetHost);
        Assert.Equal("host-5.test", latest[^1].TargetHost);
    }
}
