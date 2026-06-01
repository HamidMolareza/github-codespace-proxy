using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GhProxy.Api.Services;

public sealed class SshDirectTcpBridge : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _configPath;
    private readonly string _host;
    private readonly string _target;
    private readonly string _token;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    private SshDirectTcpBridge(
        TcpListener listener,
        string configPath,
        string host,
        string target,
        string token,
        IReadOnlyDictionary<string, string?> environment,
        ILogger logger)
    {
        _listener = listener;
        _configPath = configPath;
        _host = host;
        _target = target;
        _token = token;
        _environment = environment;
        _logger = logger;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Task Completion => _acceptLoop;

    public static SshDirectTcpBridge Start(
        int localPort,
        string configPath,
        string host,
        string target,
        string token,
        IReadOnlyDictionary<string, string?> environment,
        ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start(128);
        return new SshDirectTcpBridge(listener, configPath, host, target, token, environment, logger);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // Listener shutdown interrupts any pending accept.
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, _stopping.Token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken bridgeStopping)
    {
        using var clientDisposer = client;
        using var connectionStopping = CancellationTokenSource.CreateLinkedTokenSource(bridgeStopping);
        using var process = StartSshProcess();
        var stderrTask = process.StandardError.ReadToEndAsync(connectionStopping.Token);

        try
        {
            await using var stream = client.GetStream();
            var clientToSsh = CopyClientToSshAsync(stream, process, connectionStopping.Token);
            var sshToClient = CopySshToClientAsync(process, client, stream, connectionStopping.Token);
            await Task.WhenAll(clientToSsh, sshToClient).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Codespace ssh -W bridge connection closed.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process can exit while the client connection is closing.
                }
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort process cleanup.
            }

            connectionStopping.Cancel();
            try
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogDebug("Codespace ssh -W exited with code {ExitCode}: {StandardError}", process.ExitCode, stderr);
                }
            }
            catch
            {
                // Stderr is diagnostic only.
            }
        }
    }

    private Process StartSshProcess()
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
                 {
                     "-F", _configPath,
                     "-o", "ControlMaster=no",
                     "-o", "ControlPath=none",
                     "-o", "ServerAliveInterval=10",
                     "-o", "ServerAliveCountMax=3",
                     "-o", "TCPKeepAlive=yes",
                     "-o", "BatchMode=yes",
                     "-W", _target,
                     _host
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in _environment)
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
        startInfo.Environment["GITHUB_TOKEN"] = _token;
        startInfo.Environment["GH_TOKEN"] = _token;
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_PROXY_BRIDGE_TOKEN_PRESENT"] = string.IsNullOrEmpty(_token) ? "0" : "1";

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static async Task CopyClientToSshAsync(Stream clientStream, Process process, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await clientStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // The ssh process may already have exited.
            }
        }
    }

    private static async Task CopySshToClientAsync(Process process, TcpClient client, Stream clientStream, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await clientStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // The client may have already disconnected.
            }
        }
    }
}
