using System.Diagnostics;
using System.Text;

namespace GhProxy.Api.Services;

public interface IXrayProcessRunner
{
    XrayProcessHandle Start(string executablePath, string configPath, string workingDirectory);
}

public sealed class XrayProcessRunner(ILogger<XrayProcessRunner> logger) : IXrayProcessRunner
{
    public XrayProcessHandle Start(string executablePath, string configPath, string workingDirectory)
    {
        var standardError = new BoundedTextBuffer(8000);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(configPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start xray process.");

        _ = Task.Run(() => ReadAsync(process.StandardError, standardError));
        _ = Task.Run(() => ReadAsync(process.StandardOutput, standardError));
        var completion = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            logger.LogInformation("Xray process {ProcessId} exited with code {ExitCode}.", process.Id, process.ExitCode);
        });

        return new XrayProcessHandle(
            process.Id,
            () => process.HasExited,
            async () =>
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            },
            completion,
            standardError.ToString);
    }

    private static async Task ReadAsync(StreamReader reader, BoundedTextBuffer buffer)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            buffer.AppendLine(line);
        }
    }

    private sealed class BoundedTextBuffer(int maxChars)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _builder = new();

        public void AppendLine(string value)
        {
            lock (_gate)
            {
                _builder.AppendLine(value);
                if (_builder.Length > maxChars)
                {
                    _builder.Remove(0, _builder.Length - maxChars);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}

public sealed class XrayProcessHandle(
    int? processId,
    Func<bool> hasExited,
    Func<Task> stopAsync,
    Task completion,
    Func<string> standardError)
{
    public int? ProcessId { get; } = processId;
    public bool HasExited => hasExited();
    public Task Completion { get; } = completion;
    public string StandardError => standardError();

    public Task StopAsync() => stopAsync();
}
