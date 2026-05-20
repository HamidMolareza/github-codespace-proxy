using GhProxy.Api.Domain;
using Microsoft.Extensions.Options;

namespace GhProxy.Api.Services;

public sealed class VpsRuntimeService(
    ICommandRunner commandRunner,
    NodeConfigRenderer configRenderer,
    AuditService audit,
    IOperationalEventSink events,
    IOptions<ProxyRuntimeOptions> options)
{
    private readonly ProxyRuntimeOptions _options = options.Value;

    public async Task<RuntimeResult> BootstrapAsync(VpsNode node, CancellationToken cancellationToken)
    {
        await events.WriteAsync(new OperationalEventWrite(
            "node.bootstrap.start",
            OperationalEventSeverity.Information,
            "Starting node bootstrap.",
            NodeId: node.Id),
            cancellationToken);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "gh-proxy", node.Id.ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var composePath = Path.Combine(tempDirectory, "docker-compose.yml");
        var configPath = Path.Combine(tempDirectory, "3proxy.cfg");
        await File.WriteAllTextAsync(composePath, configRenderer.RenderCompose(node), cancellationToken);
        await File.WriteAllTextAsync(configPath, configRenderer.RenderProxyConfig(node), cancellationToken);

        var mkdir = await commandRunner.RunAsync(Ssh(node, $"mkdir -p {_options.RemoteDirectory}", "ssh.mkdir"), cancellationToken);
        if (!mkdir.Succeeded)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "node.bootstrap.failure",
                OperationalEventSeverity.Error,
                "Failed to create remote directory.",
                NodeId: node.Id,
                StandardError: mkdir.StandardError),
                cancellationToken);
            return RuntimeResult.Fail(mkdir.StandardError);
        }

        var copy = await commandRunner.RunAsync(Scp(node, composePath, configPath), cancellationToken);
        if (!copy.Succeeded)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "node.bootstrap.failure",
                OperationalEventSeverity.Error,
                "Failed to copy proxy configuration.",
                NodeId: node.Id,
                StandardError: copy.StandardError),
                cancellationToken);
            return RuntimeResult.Fail(copy.StandardError);
        }

        var start = await StartRemoteAsync(node, cancellationToken);
        if (start.Succeeded)
        {
            await audit.WriteAsync("node.bootstrap", "Bootstrapped and started proxy service.", node.Id, cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "node.bootstrap.success",
                OperationalEventSeverity.Information,
                "Bootstrapped and started proxy service.",
                NodeId: node.Id),
                cancellationToken);
        }

        return start;
    }

    public async Task<RuntimeResult> StartRemoteAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose up -d", "docker.compose.up"), cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            result.Succeeded ? "node.remote.start.success" : "node.remote.start.failure",
            result.Succeeded ? OperationalEventSeverity.Information : OperationalEventSeverity.Error,
            result.Succeeded ? "Remote proxy started." : "Remote proxy start failed.",
            NodeId: node.Id,
            StandardError: result.StandardError),
            cancellationToken);
        return result.Succeeded ? RuntimeResult.Ok("Remote proxy started.") : RuntimeResult.Fail(result.StandardError);
    }

    public async Task<RuntimeResult> StopRemoteAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose down", "docker.compose.down"), cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            result.Succeeded ? "node.remote.stop.success" : "node.remote.stop.failure",
            result.Succeeded ? OperationalEventSeverity.Information : OperationalEventSeverity.Error,
            result.Succeeded ? "Remote proxy stopped." : "Remote proxy stop failed.",
            NodeId: node.Id,
            StandardError: result.StandardError),
            cancellationToken);
        return result.Succeeded ? RuntimeResult.Ok("Remote proxy stopped.") : RuntimeResult.Fail(result.StandardError);
    }

    public async Task<RuntimeResult> ProbeStatusAsync(VpsNode node, CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(Ssh(node, $"cd {_options.RemoteDirectory} && docker compose ps --status running --services", "docker.compose.ps"), cancellationToken);
        if (!result.Succeeded)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "node.status.failure",
                OperationalEventSeverity.Error,
                "Failed to probe remote proxy status.",
                NodeId: node.Id,
                StandardError: result.StandardError),
                cancellationToken);
            return RuntimeResult.Fail(result.StandardError);
        }

        var running = result.StandardOutput.Contains("gh-proxy", StringComparison.Ordinal);
        await events.WriteAsync(new OperationalEventWrite(
            running ? "node.status.running" : "node.status.stopped",
            running ? OperationalEventSeverity.Information : OperationalEventSeverity.Warning,
            running ? "Remote proxy is running." : "Remote proxy is not running.",
            NodeId: node.Id,
            StandardOutput: result.StandardOutput),
            cancellationToken);
        return running
            ? RuntimeResult.Ok("Remote proxy is running.")
            : RuntimeResult.Fail("Remote proxy is not running.");
    }

    public CommandSpec TunnelCommand(VpsNode node, Guid? sessionId = null)
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
            ],
            Kind: "autossh.tunnel",
            NodeId: node.Id,
            SessionId: sessionId);
    }

    private static CommandSpec Ssh(VpsNode node, string remoteCommand, string kind)
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
            TimeSpan.FromSeconds(60),
            kind,
            node.Id);
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
            TimeSpan.FromSeconds(60),
            "scp.config",
            node.Id);
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
