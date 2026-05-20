using GhProxy.Api.Domain;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class VpsRuntimeService(
    ICommandRunner commandRunner,
    NodeConfigRenderer configRenderer,
    AuditService audit,
    IOptions<ProxyRuntimeOptions> options)
{
    private readonly ProxyRuntimeOptions _options = options.Value;

    public async Task<RuntimeResult> BootstrapAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "gh-proxy", node.Id.ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var composePath = Path.Combine(tempDirectory, "docker-compose.yml");
        var configPath = Path.Combine(tempDirectory, "3proxy.cfg");
        await File.WriteAllTextAsync(composePath, configRenderer.RenderCompose(node), cancellationToken);
        await File.WriteAllTextAsync(configPath, configRenderer.RenderProxyConfig(node), cancellationToken);

        var mkdir = await commandRunner.RunAsync(Ssh(node, $"mkdir -p {_options.RemoteDirectory}"), cancellationToken);
        if (!mkdir.Succeeded)
        {
            return RuntimeResult.Fail(mkdir.StandardError);
        }

        var copy = await commandRunner.RunAsync(Scp(node, composePath, configPath), cancellationToken);
        if (!copy.Succeeded)
        {
            return RuntimeResult.Fail(copy.StandardError);
        }

        var start = await StartRemoteAsync(node, cancellationToken);
        if (start.Succeeded)
        {
            await audit.WriteAsync("node.bootstrap", "Bootstrapped and started proxy service.", node.Id, cancellationToken);
        }

        return start;
    }

    public async Task<RuntimeResult> StartRemoteAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose up -d"), cancellationToken);
        return result.Succeeded ? RuntimeResult.Ok("Remote proxy started.") : RuntimeResult.Fail(result.StandardError);
    }

    public async Task<RuntimeResult> StopRemoteAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose down"), cancellationToken);
        return result.Succeeded ? RuntimeResult.Ok("Remote proxy stopped.") : RuntimeResult.Fail(result.StandardError);
    }

    public async Task<RuntimeResult> ProbeStatusAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose ps --status running --services"), cancellationToken);
        if (!result.Succeeded)
        {
            return RuntimeResult.Fail(result.StandardError);
        }

        return result.StandardOutput.Contains("gh-proxy", StringComparison.Ordinal)
            ? RuntimeResult.Ok("Remote proxy is running.")
            : RuntimeResult.Fail("Remote proxy is not running.");
    }

    public CommandSpec TunnelCommand(VpsNode node)
    {
        return new CommandSpec("autossh",
            [
                "-M", "0",
                "-N",
                "-L", $"127.0.0.1:{node.LocalPort}:127.0.0.1:{node.RemoteHttpPort}",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=10",
                "-o", "ServerAliveCountMax=3",
                "-o", "TCPKeepAlive=yes",
                "-i", ExpandHome(node.SshKeyPath),
                "-p", node.SshPort.ToString(),
                $"{node.SshUsername}@{node.Host}"
            ]);
    }

    private static CommandSpec Ssh(VpsNode node, string remoteCommand)
    {
        return new CommandSpec("ssh",
            [
                "-i", ExpandHome(node.SshKeyPath),
                "-p", node.SshPort.ToString(),
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                $"{node.SshUsername}@{node.Host}",
                remoteCommand
            ],
            TimeSpan.FromSeconds(60));
    }

    private CommandSpec Scp(VpsNode node, string composePath, string configPath)
    {
        return new CommandSpec("scp",
            [
                "-i", ExpandHome(node.SshKeyPath),
                "-P", node.SshPort.ToString(),
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                composePath,
                configPath,
                $"{node.SshUsername}@{node.Host}:{_options.RemoteDirectory}/"
            ],
            TimeSpan.FromSeconds(60));
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }
}

public sealed record RuntimeResult(bool Succeeded, string Message)
{
    public static RuntimeResult Ok(string message) => new(true, message);
    public static RuntimeResult Fail(string message) => new(false, string.IsNullOrWhiteSpace(message) ? "Command failed." : message);
}
