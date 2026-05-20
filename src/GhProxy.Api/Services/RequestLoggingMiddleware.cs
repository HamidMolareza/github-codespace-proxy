using System.Diagnostics;

namespace GhProxy.Api.Services;

public sealed class RequestLoggingMiddleware(RequestDelegate next, IOperationalEventSink events)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await next(context);
        stopwatch.Stop();

        if (ShouldSkip(context.Request.Path))
        {
            return;
        }

        var statusCode = context.Response.StatusCode;
        await events.WriteAsync(new OperationalEventWrite(
            statusCode >= 500 ? "api.request.error" : statusCode >= 400 ? "api.request.warning" : "api.request",
            statusCode >= 500 ? OperationalEventSeverity.Error : statusCode >= 400 ? OperationalEventSeverity.Warning : OperationalEventSeverity.Information,
            $"{context.Request.Method} {context.Request.Path} returned {statusCode}.",
            Duration: stopwatch.Elapsed,
            Details: new { context.Request.Method, Path = context.Request.Path.Value, statusCode }),
            CancellationToken.None);
    }

    private static bool ShouldSkip(PathString path)
    {
        return path.StartsWithSegments("/api/activity") || path.StartsWithSegments("/api/health");
    }
}
