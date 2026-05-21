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

public sealed record LocalProxySettingsRequest(
    string BindHost,
    int LocalPort,
    string? ProxyUsername,
    string? ProxyPassword,
    int IdleShutdownMinutes);

public sealed record LocalProxyAutomationStatusResponse(
    LocalProxyProfileResponse Settings,
    LocalProxySessionResponse? Session,
    string Phase,
    string? SelectedAccount,
    string? SelectedCodespace,
    string? Warning,
    DateTimeOffset? NextRetryAt,
    string? LastError,
    string Availability,
    string Message,
    string Severity,
    bool PublicPortOpen,
    int? RetryInSeconds,
    DateTimeOffset? LastRequestAt);

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
    DateTimeOffset? LastRequestAt,
    DateTimeOffset IdleShutdownAt,
    DateTimeOffset? StoppedAt,
    string? LastError,
    long TotalRequests,
    long TotalConnectTunnels,
    long TotalBytesReceived,
    long TotalBytesSent,
    int ActiveConnections,
    Guid? AccountId,
    string? CodespaceName,
    int? RemoteProxyPort,
    int? LocalTunnelPort);

public sealed record LocalProxyRuntimeResultResponse(
    bool Succeeded,
    string Message,
    LocalProxySessionResponse? Session);

public sealed record LocalProxyProbeRequest(string? ProxyUrl);

public sealed record CodespaceProxyStartRequest(Guid? ProfileId);
