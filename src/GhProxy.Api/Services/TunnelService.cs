using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class TunnelService(
    AppDbContext db,
    VpsRuntimeService runtime,
    ICommandRunner commandRunner,
    AuditService audit,
    IClock clock,
    IOperationalEventSink events)
{
    public async Task<ProxySession> StartAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var node = await db.VpsNodes.FirstOrDefaultAsync(x => x.Id == nodeId, cancellationToken)
            ?? throw new InvalidOperationException("VPS node was not found.");

        var activeSession = await db.ProxySessions
            .Where(x => x.Status == ProxySessionStatus.Running || x.Status == ProxySessionStatus.Starting)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeSession is not null)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "session.start.reused",
                OperationalEventSeverity.Warning,
                "An active proxy session already exists.",
                NodeId: activeSession.NodeId,
                SessionId: activeSession.Id),
                cancellationToken);
            return activeSession;
        }

        var now = clock.UtcNow;
        var session = new ProxySession
        {
            NodeId = node.Id,
            Status = ProxySessionStatus.Starting,
            LocalPort = node.LocalPort,
            RemotePort = node.RemoteHttpPort,
            StartedAt = now,
            LastActivityAt = now
        };
        db.ProxySessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "session.start.requested",
            OperationalEventSeverity.Information,
            "Proxy session start requested.",
            NodeId: node.Id,
            SessionId: session.Id),
            cancellationToken);

        var remoteStart = await runtime.StartRemoteAsync(node, cancellationToken);
        if (!remoteStart.Succeeded)
        {
            session.Status = ProxySessionStatus.Error;
            session.LastError = remoteStart.Message;
            node.Status = VpsNodeStatus.Error;
            await db.SaveChangesAsync(cancellationToken);
            await events.WriteAsync(new OperationalEventWrite(
                "session.start.failure",
                OperationalEventSeverity.Error,
                remoteStart.Message,
                NodeId: node.Id,
                SessionId: session.Id),
                cancellationToken);
            return session;
        }

        var process = commandRunner.Start(runtime.TunnelCommand(node, session.Id));
        session.TunnelProcessId = process.Id;
        session.Status = ProxySessionStatus.Running;
        node.Status = VpsNodeStatus.Running;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("session.start", $"Started tunnel on 127.0.0.1:{node.LocalPort}.", node.Id, cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "session.start.success",
            OperationalEventSeverity.Information,
            $"Started tunnel on 127.0.0.1:{node.LocalPort}.",
            NodeId: node.Id,
            SessionId: session.Id,
            Details: new { process.Id, node.LocalPort, node.RemoteHttpPort }),
            cancellationToken);
        return session;
    }

    public async Task<ProxySession?> StopActiveAsync(CancellationToken cancellationToken)
    {
        var session = await db.ProxySessions
            .Include(x => x.Node)
            .Where(x => x.Status == ProxySessionStatus.Running || x.Status == ProxySessionStatus.Starting || x.Status == ProxySessionStatus.Error)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session is null)
        {
            await events.WriteAsync(new OperationalEventWrite(
                "session.stop.none",
                OperationalEventSeverity.Warning,
                "Stop requested but no active proxy session was found."),
                cancellationToken);
            return null;
        }

        await StopSessionAsync(session, "Stopped by user.", cancellationToken);
        return session;
    }

    public async Task StopSessionAsync(ProxySession session, string reason, CancellationToken cancellationToken)
    {
        session.Status = ProxySessionStatus.Stopping;
        await db.SaveChangesAsync(cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "session.stop.requested",
            OperationalEventSeverity.Information,
            reason,
            NodeId: session.NodeId,
            SessionId: session.Id),
            cancellationToken);

        if (session.TunnelProcessId is int pid)
        {
            var killed = TryKill(pid);
            await events.WriteAsync(new OperationalEventWrite(
                killed ? "session.tunnel.kill" : "session.tunnel.kill.skipped",
                killed ? OperationalEventSeverity.Information : OperationalEventSeverity.Warning,
                killed ? "Killed local tunnel process." : "Local tunnel process was already gone.",
                NodeId: session.NodeId,
                SessionId: session.Id,
                Details: new { pid }),
                cancellationToken);
        }

        if (session.Node is not null)
        {
            var remoteStop = await runtime.StopRemoteAsync(session.Node, cancellationToken);
            session.Node.Status = remoteStop.Succeeded ? VpsNodeStatus.Stopped : VpsNodeStatus.Error;
            if (!remoteStop.Succeeded)
            {
                session.LastError = remoteStop.Message;
            }
        }

        session.Status = ProxySessionStatus.Stopped;
        session.StoppedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("session.stop", reason, session.NodeId, cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "session.stop.completed",
            OperationalEventSeverity.Information,
            reason,
            NodeId: session.NodeId,
            SessionId: session.Id),
            cancellationToken);
    }

    public async Task<ProxySession?> GetActiveAsync(CancellationToken cancellationToken)
    {
        return await db.ProxySessions
            .Include(x => x.Node)
            .Where(x => x.Status == ProxySessionStatus.Running || x.Status == ProxySessionStatus.Starting)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task MarkActivityAsync(ProxySession session, CancellationToken cancellationToken)
    {
        session.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await events.WriteAsync(new OperationalEventWrite(
            "session.activity",
            OperationalEventSeverity.Debug,
            "Detected active local tunnel connection.",
            NodeId: session.NodeId,
            SessionId: session.Id),
            cancellationToken);
    }

    private static bool TryKill(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            // The tunnel may already have exited.
            return false;
        }
    }
}
