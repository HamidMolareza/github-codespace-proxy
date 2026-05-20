using System.Text.Json;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed record OperationalEventWrite(
    string EventType,
    string Severity,
    string Message,
    Guid? NodeId = null,
    Guid? SessionId = null,
    string? CommandKind = null,
    string? CommandDisplay = null,
    int? ExitCode = null,
    TimeSpan? Duration = null,
    bool TimedOut = false,
    string? StandardOutput = null,
    string? StandardError = null,
    object? Details = null);

public interface IOperationalEventSink
{
    Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default);
}

public sealed class OperationalEventSink(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ICorrelationContext correlation,
    ISensitiveDataRedactor redactor,
    IOptions<ObservabilityOptions> options,
    ILogger<OperationalEventSink> logger)
    : IOperationalEventSink
{
    private readonly ObservabilityOptions _options = options.Value;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default)
    {
        var maxOutputChars = Math.Max(128, _options.MaxOutputChars);
        var evt = new OperationalEvent
        {
            Timestamp = clock.UtcNow,
            Severity = redactor.Redact(entry.Severity),
            EventType = redactor.Redact(entry.EventType),
            Message = redactor.Redact(entry.Message),
            NodeId = entry.NodeId,
            SessionId = entry.SessionId,
            CorrelationId = correlation.CorrelationId,
            CommandKind = redactor.Redact(entry.CommandKind),
            CommandDisplay = redactor.Redact(entry.CommandDisplay),
            ExitCode = entry.ExitCode,
            DurationMs = entry.Duration is null ? null : (long)entry.Duration.Value.TotalMilliseconds,
            TimedOut = entry.TimedOut,
            StandardOutputSnippet = redactor.Snippet(entry.StandardOutput, maxOutputChars),
            StandardErrorSnippet = redactor.Snippet(entry.StandardError, maxOutputChars),
            DetailsJson = entry.Details is null ? null : redactor.Snippet(JsonSerializer.Serialize(entry.Details), maxOutputChars)
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OperationalEvents.Add(evt);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist operational event {EventType}.", evt.EventType);
        }

        if (_options.EnableJsonlFile)
        {
            await WriteJsonlAsync(evt, cancellationToken);
        }
    }

    private async Task WriteJsonlAsync(OperationalEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetFullPath(_options.LogDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"operational-{evt.Timestamp:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(evt) + Environment.NewLine;
            await _fileLock.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(path, line, cancellationToken);
            }
            finally
            {
                _fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write operational JSONL log.");
        }
    }
}

public static class OperationalEventSeverity
{
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
}
