using GhProxy.Api.Domain;

namespace GhProxy.Api.Contracts;

public sealed record VpsNodeRequest(
    string Name,
    string Host,
    int SshPort,
    string SshUsername,
    string SshKeyPath,
    string? Region,
    string? Notes,
    int LocalPort,
    int RemoteHttpPort,
    int RemoteSocksPort,
    string ProxyUsername,
    string? ProxyPassword);

public sealed record VpsNodeResponse(
    Guid Id,
    string Name,
    string Host,
    int SshPort,
    string SshUsername,
    string SshKeyPath,
    string? Region,
    string? Notes,
    int LocalPort,
    int RemoteHttpPort,
    int RemoteSocksPort,
    string ProxyUsername,
    VpsNodeStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProxySessionResponse(
    Guid Id,
    Guid NodeId,
    string NodeName,
    ProxySessionStatus Status,
    int? TunnelProcessId,
    int LocalPort,
    int RemotePort,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? StoppedAt,
    string? LastError);

public sealed record RuntimeResultResponse(bool Succeeded, string Message);
