namespace GhProxy.Api.Services;

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlation)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        correlation.CorrelationId = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            try
            {
                await next(context);
            }
            finally
            {
                correlation.CorrelationId = null;
            }
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            return incoming.Trim();
        }

        return context.TraceIdentifier;
    }
}
