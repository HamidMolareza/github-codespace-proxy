using System.Text.Json;

namespace GhProxy.Api.Services;

public sealed class XrayConfigRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Render(XrayConfigRequest request)
    {
        var httpSettings = new Dictionary<string, object?>();
        var socksSettings = new Dictionary<string, object?>
        {
            ["udp"] = true,
            ["auth"] = request.RequiresAuthentication ? "password" : "noauth"
        };

        if (request.RequiresAuthentication)
        {
            var accounts = new[]
            {
                new Dictionary<string, string>
                {
                    ["user"] = request.ProxyUsername!,
                    ["pass"] = request.ProxyPassword!
                }
            };
            httpSettings["accounts"] = accounts;
            socksSettings["accounts"] = accounts;
        }

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["loglevel"] = "warning",
                ["access"] = request.AccessLogPath,
                ["error"] = request.ErrorLogPath
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "local-http",
                    ["listen"] = request.ListenHost,
                    ["port"] = request.HttpPort,
                    ["protocol"] = "http",
                    ["settings"] = httpSettings
                },
                new Dictionary<string, object?>
                {
                    ["tag"] = "local-socks",
                    ["listen"] = request.ListenHost,
                    ["port"] = request.SocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = socksSettings
                }
            },
            ["outbounds"] = new object[] { BuildOutbound(request) },
            ["routing"] = new Dictionary<string, object?>
            {
                ["rules"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new[] { "local-http", "local-socks" },
                        ["outboundTag"] = request.UpstreamProxy is null ? "direct" : "codespace-proxy"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static Dictionary<string, object?> BuildOutbound(XrayConfigRequest request)
    {
        if (request.UpstreamProxy is null)
        {
            return new Dictionary<string, object?>
            {
                ["tag"] = "direct",
                ["protocol"] = "freedom"
            };
        }

        var upstream = request.UpstreamProxy;
        return new Dictionary<string, object?>
        {
            ["tag"] = "codespace-proxy",
            ["protocol"] = upstream.Protocol,
            ["settings"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = upstream.Host,
                        ["port"] = upstream.Port
                    }
                }
            }
        };
    }
}

public sealed record XrayConfigRequest(
    string ListenHost,
    int HttpPort,
    int SocksPort,
    string AccessLogPath,
    string ErrorLogPath,
    string? ProxyUsername,
    string? ProxyPassword,
    XrayOutboundProxy? UpstreamProxy = null)
{
    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ProxyUsername) && !string.IsNullOrWhiteSpace(ProxyPassword);
}

public sealed record XrayOutboundProxy(string Protocol, string Host, int Port);
