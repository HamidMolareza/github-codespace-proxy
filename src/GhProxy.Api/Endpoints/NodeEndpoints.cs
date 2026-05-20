using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nodes");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await db.VpsNodes.OrderBy(x => x.Name).Select(x => ToResponse(x)).ToListAsync(ct));

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var node = await db.VpsNodes.FindAsync([id], ct);
            return node is null ? Results.NotFound() : Results.Ok(ToResponse(node));
        });

        group.MapPost("/", async (VpsNodeRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var validation = Validate(request, requirePassword: true);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var now = clock.UtcNow;
            var node = new VpsNode
            {
                Name = request.Name.Trim(),
                Host = request.Host.Trim(),
                SshPort = request.SshPort,
                SshUsername = request.SshUsername.Trim(),
                SshKeyPath = request.SshKeyPath.Trim(),
                Region = NullIfEmpty(request.Region),
                Notes = NullIfEmpty(request.Notes),
                LocalPort = request.LocalPort,
                RemoteHttpPort = request.RemoteHttpPort,
                RemoteSocksPort = request.RemoteSocksPort,
                ProxyUsername = request.ProxyUsername.Trim(),
                ProtectedProxyPassword = secrets.Protect(request.ProxyPassword!.Trim()),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.VpsNodes.Add(node);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("node.create", "Created VPS node.", node.Id, ct);
            return Results.Created($"/api/nodes/{node.Id}", ToResponse(node));
        });

        group.MapPut("/{id:guid}", async (Guid id, VpsNodeRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var node = await db.VpsNodes.FindAsync([id], ct);
            if (node is null)
            {
                return Results.NotFound();
            }

            var validation = Validate(request, requirePassword: false);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            node.Name = request.Name.Trim();
            node.Host = request.Host.Trim();
            node.SshPort = request.SshPort;
            node.SshUsername = request.SshUsername.Trim();
            node.SshKeyPath = request.SshKeyPath.Trim();
            node.Region = NullIfEmpty(request.Region);
            node.Notes = NullIfEmpty(request.Notes);
            node.LocalPort = request.LocalPort;
            node.RemoteHttpPort = request.RemoteHttpPort;
            node.RemoteSocksPort = request.RemoteSocksPort;
            node.ProxyUsername = request.ProxyUsername.Trim();
            node.UpdatedAt = clock.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.ProxyPassword))
            {
                node.ProtectedProxyPassword = secrets.Protect(request.ProxyPassword.Trim());
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("node.update", "Updated VPS node.", node.Id, ct);
            return Results.Ok(ToResponse(node));
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var node = await db.VpsNodes.FindAsync([id], ct);
            if (node is null)
            {
                return Results.NotFound();
            }

            db.VpsNodes.Remove(node);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("node.delete", "Deleted VPS node.", id, ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/bootstrap", async (Guid id, AppDbContext db, VpsRuntimeService runtime, AuditService audit, CancellationToken ct) =>
        {
            var node = await db.VpsNodes.FindAsync([id], ct);
            if (node is null)
            {
                return Results.NotFound();
            }

            var result = await runtime.BootstrapAsync(node, ct);
            node.Status = result.Succeeded ? VpsNodeStatus.Ready : VpsNodeStatus.Error;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("node.bootstrap.result", result.Message, node.Id, ct);
            return Results.Ok(new RuntimeResultResponse(result.Succeeded, result.Message));
        });

        group.MapPost("/{id:guid}/status", async (Guid id, AppDbContext db, VpsRuntimeService runtime, CancellationToken ct) =>
        {
            var node = await db.VpsNodes.FindAsync([id], ct);
            if (node is null)
            {
                return Results.NotFound();
            }

            var result = await runtime.ProbeStatusAsync(node, ct);
            node.Status = result.Succeeded ? VpsNodeStatus.Running : VpsNodeStatus.Stopped;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RuntimeResultResponse(result.Succeeded, result.Message));
        });

        return app;
    }

    private static VpsNodeResponse ToResponse(VpsNode node)
    {
        return new VpsNodeResponse(
            node.Id,
            node.Name,
            node.Host,
            node.SshPort,
            node.SshUsername,
            node.SshKeyPath,
            node.Region,
            node.Notes,
            node.LocalPort,
            node.RemoteHttpPort,
            node.RemoteSocksPort,
            node.ProxyUsername,
            node.Status,
            node.CreatedAt,
            node.UpdatedAt);
    }

    private static object? Validate(VpsNodeRequest request, bool requirePassword)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.Name), request.Name);
        AddRequired(errors, nameof(request.Host), request.Host);
        AddRequired(errors, nameof(request.SshUsername), request.SshUsername);
        AddRequired(errors, nameof(request.SshKeyPath), request.SshKeyPath);
        AddRequired(errors, nameof(request.ProxyUsername), request.ProxyUsername);
        if (requirePassword)
        {
            AddRequired(errors, nameof(request.ProxyPassword), request.ProxyPassword);
        }

        AddPort(errors, nameof(request.SshPort), request.SshPort);
        AddPort(errors, nameof(request.LocalPort), request.LocalPort);
        AddPort(errors, nameof(request.RemoteHttpPort), request.RemoteHttpPort);
        AddPort(errors, nameof(request.RemoteSocksPort), request.RemoteSocksPort);
        return errors.Count == 0 ? null : new { errors };
    }

    private static void AddRequired(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Value is required."];
        }
    }

    private static void AddPort(Dictionary<string, string[]> errors, string field, int value)
    {
        if (value is < 1 or > 65535)
        {
            errors[field] = ["Port must be between 1 and 65535."];
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
