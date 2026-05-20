using System.Diagnostics;

namespace GhProxy.Api.Services;

public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan? Timeout = null)
{
    public string RedactedDisplay => string.Join(" ", new[] { FileName }.Concat(Arguments.Select(Redact)));

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

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken);
    Process Start(CommandSpec command);
}

public sealed class ProcessCommandRunner(ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(command);
        logger.LogInformation("Running command: {Command}", command.RedactedDisplay);
        process.Start();

        var timeout = command.Timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            return new CommandResult(124, "", $"Command timed out after {timeout.TotalSeconds:n0}s.");
        }
    }

    public Process Start(CommandSpec command)
    {
        var process = CreateProcess(command);
        process.StartInfo.RedirectStandardOutput = false;
        process.StartInfo.RedirectStandardError = false;
        process.StartInfo.RedirectStandardInput = false;
        logger.LogInformation("Starting command: {Command}", command.RedactedDisplay);
        process.Start();
        return process;
    }

    private static Process CreateProcess(CommandSpec command)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false
        };

        foreach (var arg in command.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return new Process { StartInfo = startInfo };
    }

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
