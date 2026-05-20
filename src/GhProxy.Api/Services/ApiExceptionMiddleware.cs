namespace GhProxy.Api.Services;

public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    ICorrelationContext correlation,
    ISensitiveDataRedactor redactor,
    IOperationalEventSink events,
    ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var message = redactor.Redact(ex.Message);
            logger.LogError(ex, "Unhandled request failure. CorrelationId={CorrelationId}", correlation.CorrelationId);
            await events.WriteAsync(new OperationalEventWrite(
                "api.unhandled_exception",
                OperationalEventSeverity.Error,
                message,
                Details: new { context.Request.Method, Path = context.Request.Path.Value }),
                context.RequestAborted);

            context.Response.StatusCode = ex switch
            {
                GitHubApiException github => github.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? StatusCodes.Status502BadGateway
                    : StatusCodes.Status502BadGateway,
                InvalidOperationException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };
            await context.Response.WriteAsJsonAsync(new
            {
                error = ex is GitHubApiException or InvalidOperationException ? message : "Unexpected server error.",
                correlationId = correlation.CorrelationId
            });
        }
    }
}
