using GhProxy.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhProxy.Tests;

public sealed class CorrelationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_UsesIncomingCorrelationHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "corr-123";
        var correlation = new CorrelationContext();
        var observed = string.Empty;
        var middleware = new CorrelationMiddleware(
            _ =>
            {
                observed = correlation.CorrelationId;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationMiddleware>.Instance);

        await middleware.InvokeAsync(context, correlation);

        Assert.Equal("corr-123", observed);
        Assert.Equal("corr-123", context.Response.Headers[CorrelationMiddleware.HeaderName]);
        Assert.Null(correlation.CorrelationId);
    }
}
