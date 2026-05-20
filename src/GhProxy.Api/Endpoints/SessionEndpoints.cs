using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions");

        group.MapGet("/active", async (TunnelService tunnelService, CancellationToken ct) =>
        {
            var session = await tunnelService.GetActiveAsync(ct);
            return session is null ? Results.Ok(null) : Results.Ok(ToResponse(session));
        });

        group.MapPost("/start/{nodeId:guid}", async (Guid nodeId, TunnelService tunnelService, CancellationToken ct) =>
        {
            var session = await tunnelService.StartAsync(nodeId, ct);
            return Results.Ok(ToResponse(session));
        });

        group.MapPost("/stop", async (TunnelService tunnelService, CancellationToken ct) =>
        {
            var session = await tunnelService.StopActiveAsync(ct);
            return session is null ? Results.Ok(null) : Results.Ok(ToResponse(session));
        });

        group.MapGet("/history", async (AppDbContext db, CancellationToken ct) =>
        {
            var sessions = await db.ProxySessions
                .Include(x => x.Node)
                .OrderByDescending(x => x.StartedAt)
                .Take(50)
                .ToListAsync(ct);
            return sessions.Select(ToResponse);
        });

        return app;
    }

    private static ProxySessionResponse ToResponse(ProxySession session)
    {
        return new ProxySessionResponse(
            session.Id,
            session.NodeId,
            session.Node?.Name ?? "Unknown",
            session.Status,
            session.TunnelProcessId,
            session.LocalPort,
            session.RemotePort,
            session.StartedAt,
            session.LastActivityAt,
            session.StoppedAt,
            session.LastError);
    }
}
