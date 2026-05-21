using System.ComponentModel;
using System.Diagnostics;

namespace GhProxy.Api.Services;

public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    string? Kind = null,
    Guid? NodeId = null,
    Guid? SessionId = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null)
{
    public string RedactedDisplay => string.Join(" ", new[] { FileName }.Concat(Arguments.Select(Redact)));
    public string CommandKind => Kind ?? FileName;

    private static string Redact(string value)
    {
        if (value.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("users ", StringComparison.OrdinalIgnoreCase))
        {
            return "<redacted>";
        }

        return value;
    }
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false, TimeSpan? Duration = null)
{
    public bool Succeeded => ExitCode == 0;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken);
    Process Start(CommandSpec command);
}

public sealed class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger, IOperationalEventSink events) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(command);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Running command {CommandKind}: {Command}", command.CommandKind, command.RedactedDisplay);
        await events.WriteAsync(new OperationalEventWrite(
            "command.start",
            OperationalEventSeverity.Information,
            $"Running command {command.CommandKind}.",
            NodeId: command.NodeId,
            SessionId: command.SessionId,
            CommandKind: command.CommandKind,
            CommandDisplay: command.RedactedDisplay),
            cancellationToken);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            stopwatch.Stop();
            var error = $"Could not start command {command.CommandKind}: {ex.Message}";
            logger.LogError(ex, "Failed to start command {CommandKind}: {Command}", command.CommandKind, command.RedactedDisplay);
            await events.WriteAsync(new OperationalEventWrite(
                "command.start.failed",
                OperationalEventSeverity.Error,
                $"Failed to start command {command.CommandKind}.",
                NodeId: command.NodeId,
                SessionId: command.SessionId,
                CommandKind: command.CommandKind,
                CommandDisplay: command.RedactedDisplay,
                ExitCode: 127,
                Duration: stopwatch.Elapsed,
                StandardError: error),
                cancellationToken);
            return new CommandResult(127, "", error, Duration: stopwatch.Elapsed);
        }

        var timeout = command.Timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            stopwatch.Stop();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await events.WriteAsync(new OperationalEventWrite(
                process.ExitCode == 0 ? "command.success" : "command.failure",
                process.ExitCode == 0 ? OperationalEventSeverity.Information : OperationalEventSeverity.Error,
                process.ExitCode == 0
                    ? $"Command {command.CommandKind} completed."
                    : $"Command {command.CommandKind} failed with exit code {process.ExitCode}.",
                NodeId: command.NodeId,
                SessionId: command.SessionId,
                CommandKind: command.CommandKind,
                CommandDisplay: command.RedactedDisplay,
                ExitCode: process.ExitCode,
                Duration: stopwatch.Elapsed,
                StandardOutput: stdout,
                StandardError: stderr),
                cancellationToken);
            return new CommandResult(process.ExitCode, stdout, stderr, Duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            TryKill(process);
            var error = $"Command timed out after {timeout.TotalSeconds:n0}s.";
            await events.WriteAsync(new OperationalEventWrite(
                "command.timeout",
                OperationalEventSeverity.Error,
                $"Command {command.CommandKind} timed out.",
                NodeId: command.NodeId,
                SessionId: command.SessionId,
                CommandKind: command.CommandKind,
                CommandDisplay: command.RedactedDisplay,
                ExitCode: 124,
                Duration: stopwatch.Elapsed,
                TimedOut: true,
                StandardError: error),
                CancellationToken.None);
            return new CommandResult(124, "", error, TimedOut: true, Duration: stopwatch.Elapsed);
        }
    }

    public Process Start(CommandSpec command)
    {
        var process = CreateProcess(command);
        process.StartInfo.RedirectStandardOutput = false;
        process.StartInfo.RedirectStandardError = false;
        process.StartInfo.RedirectStandardInput = false;
        logger.LogInformation("Starting command {CommandKind}: {Command}", command.CommandKind, command.RedactedDisplay);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (IsProcessStartException(ex))
        {
            var error = $"Could not start command {command.CommandKind}: {ex.Message}";
            logger.LogError(ex, "Failed to start command {CommandKind}: {Command}", command.CommandKind, command.RedactedDisplay);
            events.WriteAsync(new OperationalEventWrite(
                "command.start.failed",
                OperationalEventSeverity.Error,
                $"Failed to start command {command.CommandKind}.",
                NodeId: command.NodeId,
                SessionId: command.SessionId,
                CommandKind: command.CommandKind,
                CommandDisplay: command.RedactedDisplay,
                ExitCode: 127,
                StandardError: error),
                CancellationToken.None).GetAwaiter().GetResult();
            process.Dispose();
            throw new InvalidOperationException(error, ex);
        }
        _ = events.WriteAsync(new OperationalEventWrite(
            "command.spawn",
            OperationalEventSeverity.Information,
            $"Started command {command.CommandKind} as process {process.Id}.",
            NodeId: command.NodeId,
            SessionId: command.SessionId,
            CommandKind: command.CommandKind,
            CommandDisplay: command.RedactedDisplay,
            Details: new { process.Id }),
            CancellationToken.None);
        return process;
    }

    private static Process CreateProcess(CommandSpec command)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            WorkingDirectory = GetValidWorkingDirectory()
        };

        foreach (var arg in command.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (command.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in command.EnvironmentVariables)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        return new Process { StartInfo = startInfo };
    }

    private static string GetValidWorkingDirectory()
    {
        var cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(cwd))
        {
            return cwd;
        }

        return Path.GetTempPath();
    }

    private static bool IsProcessStartException(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup for timed-out child processes.
        }
    }
}
