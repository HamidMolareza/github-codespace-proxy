using GhProxy.Api.Domain;

namespace GhProxy.Api.Contracts;

public sealed record LocalProxyProfileRequest(
    string Name,
    string BindHost,
    int LocalPort,
    int? SocksPort,
    string? ProxyUsername,
    string? ProxyPassword,
    int IdleShutdownMinutes,
    string? Notes);

public sealed record LocalProxyProfileResponse(
    Guid Id,
    string Name,
    string BindHost,
    int LocalPort,
    int SocksPort,
    string? ProxyUsername,
    bool RequiresAuthentication,
    int IdleShutdownMinutes,
    string? Notes,
    LocalProxyProfileStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LocalProxySessionResponse(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    LocalProxySessionStatus Status,
    string BindHost,
    int LocalPort,
    int SocksPort,
    string ProxyUrl,
    string HttpProxyUrl,
    string SocksProxyUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset IdleShutdownAt,
    DateTimeOffset? StoppedAt,
    string? LastError,
    long TotalRequests,
    long TotalConnectTunnels,
    long TotalBytesReceived,
    long TotalBytesSent,
    int ActiveConnections);

public sealed record LocalProxyRuntimeResultResponse(
    bool Succeeded,
    string Message,
    LocalProxySessionResponse? Session);

public sealed record LocalProxyProbeRequest(string? ProxyUrl);

public sealed record CodespaceProxyStartRequest(Guid? ProfileId);
