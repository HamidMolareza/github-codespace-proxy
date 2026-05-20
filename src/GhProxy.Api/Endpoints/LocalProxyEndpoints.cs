using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Endpoints;

public static class LocalProxyEndpoints
{
    public static IEndpointRouteBuilder MapLocalProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/local-proxy");

        group.MapGet("/profiles", async (AppDbContext db, CancellationToken ct) =>
            await db.LocalProxyProfiles
                .OrderBy(x => x.Name)
                .Select(x => ToResponse(x))
                .ToListAsync(ct));

        group.MapPost("/profiles", async (LocalProxyProfileRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var socksPort = request.SocksPort ?? request.LocalPort;
            var validation = await ValidateAsync(request, socksPort, db, null, requirePasswordForNewAuth: true, ct);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var name = request.Name.Trim();
            if (await db.LocalProxyProfiles.AnyAsync(x => x.Name == name, ct))
            {
                return Results.Conflict(new { error = "A local proxy profile with this name already exists." });
            }

            var now = clock.UtcNow;
            var profile = new LocalProxyProfile
            {
                Name = name,
                BindHost = request.BindHost.Trim(),
                LocalPort = request.LocalPort,
                SocksPort = socksPort,
                ProxyUsername = NullIfEmpty(request.ProxyUsername),
                ProtectedProxyPassword = string.IsNullOrWhiteSpace(request.ProxyPassword) ? null : secrets.Protect(request.ProxyPassword.Trim()),
                IdleShutdownMinutes = request.IdleShutdownMinutes,
                Notes = NullIfEmpty(request.Notes),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.LocalProxyProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.create", "Created local proxy profile.", null, ct);
            return Results.Created($"/api/local-proxy/profiles/{profile.Id}", ToResponse(profile));
        });

        group.MapPut("/profiles/{id:guid}", async (Guid id, LocalProxyProfileRequest request, AppDbContext db, ISecretProtector secrets, IClock clock, AuditService audit, CancellationToken ct) =>
        {
            var profile = await db.LocalProxyProfiles.FindAsync([id], ct);
            if (profile is null)
            {
                return Results.NotFound();
            }

            var wantsAuth = !string.IsNullOrWhiteSpace(request.ProxyUsername);
            var hasExistingPassword = !string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword);
            var socksPort = request.SocksPort ?? request.LocalPort;
            var validation = await ValidateAsync(request, socksPort, db, id, requirePasswordForNewAuth: wantsAuth && !hasExistingPassword, ct);
            if (validation is not null)
            {
                return Results.BadRequest(validation);
            }

            var name = request.Name.Trim();
            if (await db.LocalProxyProfiles.AnyAsync(x => x.Id != id && x.Name == name, ct))
            {
                return Results.Conflict(new { error = "A local proxy profile with this name already exists." });
            }

            profile.Name = name;
            profile.BindHost = request.BindHost.Trim();
            profile.LocalPort = request.LocalPort;
            profile.SocksPort = socksPort;
            profile.ProxyUsername = NullIfEmpty(request.ProxyUsername);
            if (string.IsNullOrWhiteSpace(request.ProxyUsername))
            {
                profile.ProtectedProxyPassword = null;
            }
            else if (!string.IsNullOrWhiteSpace(request.ProxyPassword))
            {
                profile.ProtectedProxyPassword = secrets.Protect(request.ProxyPassword.Trim());
            }

            profile.IdleShutdownMinutes = request.IdleShutdownMinutes;
            profile.Notes = NullIfEmpty(request.Notes);
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.update", "Updated local proxy profile.", null, ct);
            return Results.Ok(ToResponse(profile));
        });

        group.MapDelete("/profiles/{id:guid}", async (Guid id, AppDbContext db, LocalProxyRuntimeService runtime, AuditService audit, CancellationToken ct) =>
        {
            var active = await runtime.GetActiveAsync(ct);
            if (active?.ProfileId == id)
            {
                return Results.Conflict(new { error = "Stop the active local proxy session before deleting this profile." });
            }

            var profile = await db.LocalProxyProfiles.FindAsync([id], ct);
            if (profile is null)
            {
                return Results.NotFound();
            }

            db.LocalProxyProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("local_proxy.profile.delete", "Deleted local proxy profile.", null, ct);
            return Results.NoContent();
        });

        group.MapGet("/session", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var session = ToResponse(await runtime.GetActiveAsync(ct));
            return session is null ? Results.Text("null", "application/json") : Results.Ok(session);
        });

        group.MapPost("/profiles/{id:guid}/start", async (Guid id, LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.StartAsync(id, ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        group.MapPost("/stop", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.StopAsync("Stopped by user.", ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        group.MapPost("/probe", async (LocalProxyRuntimeService runtime, CancellationToken ct) =>
        {
            var result = await runtime.ProbeActiveAsync(ct);
            return Results.Ok(new LocalProxyRuntimeResultResponse(result.Succeeded, result.Message, ToResponse(result.Session)));
        });

        return app;
    }

    private static LocalProxyProfileResponse ToResponse(LocalProxyProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.BindHost,
            profile.LocalPort,
            profile.SocksPort,
            profile.ProxyUsername,
            !string.IsNullOrWhiteSpace(profile.ProxyUsername) && !string.IsNullOrWhiteSpace(profile.ProtectedProxyPassword),
            profile.IdleShutdownMinutes,
            profile.Notes,
            profile.Status,
            profile.CreatedAt,
            profile.UpdatedAt);

    public static LocalProxySessionResponse? ToResponse(LocalProxyRuntimeState? state) =>
        state is null
            ? null
            : new LocalProxySessionResponse(
                state.Id,
                state.ProfileId,
                state.ProfileName,
                state.Status,
                state.BindHost,
                state.LocalPort,
                state.SocksPort,
                state.ProxyUrl,
                state.HttpProxyUrl,
                state.SocksProxyUrl,
                state.StartedAt,
                state.LastActivityAt,
                state.IdleShutdownAt,
                state.StoppedAt,
                state.LastError,
                state.TotalRequests,
                state.TotalConnectTunnels,
                state.TotalBytesReceived,
                state.TotalBytesSent,
                state.ActiveConnections);

    private static async Task<object?> ValidateAsync(LocalProxyProfileRequest request, int socksPort, AppDbContext db, Guid? existingProfileId, bool requirePasswordForNewAuth, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequired(errors, nameof(request.Name), request.Name);
        AddRequired(errors, nameof(request.BindHost), request.BindHost);
        AddPort(errors, nameof(request.LocalPort), request.LocalPort);
        AddPort(errors, nameof(request.SocksPort), socksPort);

        var portOwner = await db.LocalProxyProfiles
            .AsNoTracking()
            .Where(x => existingProfileId == null || x.Id != existingProfileId)
            .Where(x => x.LocalPort == request.LocalPort ||
                        x.SocksPort == request.LocalPort)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);
        if (portOwner is not null)
        {
            errors[nameof(request.LocalPort)] = [$"Proxy port overlaps with profile \"{portOwner}\"."];
        }

        if (request.IdleShutdownMinutes is < 1 or > 1440)
        {
            errors[nameof(request.IdleShutdownMinutes)] = ["Idle shutdown must be between 1 and 1440 minutes."];
        }

        var hasUsername = !string.IsNullOrWhiteSpace(request.ProxyUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(request.ProxyPassword);
        if (hasPassword && !hasUsername)
        {
            errors[nameof(request.ProxyUsername)] = ["Username is required when password is set."];
        }

        if (requirePasswordForNewAuth && hasUsername && !hasPassword)
        {
            errors[nameof(request.ProxyPassword)] = ["Password is required when enabling proxy authentication."];
        }

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
