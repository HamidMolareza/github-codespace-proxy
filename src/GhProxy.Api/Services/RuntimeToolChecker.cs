namespace GhProxy.Api.Services;

public sealed record RuntimeToolDiagnostic(string Name, string Command, bool Available, string Message);

public sealed record RuntimeToolCheckResult(IReadOnlyList<RuntimeToolDiagnostic> Tools)
{
    public bool Succeeded => Tools.All(x => x.Available);

    public string MissingSummary =>
        string.Join(", ", Tools.Where(x => !x.Available).Select(x => x.Command));
}

public interface IRuntimeToolChecker
{
    RuntimeToolCheckResult CheckCodespaceProxyTools(string xrayExecutablePath);
    IReadOnlyList<RuntimeToolDiagnostic> GetRuntimeDiagnostics(string xrayExecutablePath);
}

public sealed class RuntimeToolChecker : IRuntimeToolChecker
{
    private static readonly (string Name, string Command)[] CodespaceProxyTools =
    [
        ("Xray", "xray"),
        ("GitHub CLI", "gh"),
        ("ssh", "ssh")
    ];

    public RuntimeToolCheckResult CheckCodespaceProxyTools(string xrayExecutablePath) =>
        new(GetRuntimeDiagnostics(xrayExecutablePath));

    public IReadOnlyList<RuntimeToolDiagnostic> GetRuntimeDiagnostics(string xrayExecutablePath) =>
        CodespaceProxyTools
            .Select(tool =>
            {
                var command = tool.Command == "xray" ? xrayExecutablePath : tool.Command;
                var resolved = ResolveExecutable(command);
                return new RuntimeToolDiagnostic(
                    tool.Name,
                    command,
                    resolved is not null,
                    resolved ?? $"Missing executable: {command}");
            })
            .ToList();

    public static string? ResolveExecutable(string executablePath)
    {
        if (Path.IsPathRooted(executablePath))
        {
            return File.Exists(executablePath) ? executablePath : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executablePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
