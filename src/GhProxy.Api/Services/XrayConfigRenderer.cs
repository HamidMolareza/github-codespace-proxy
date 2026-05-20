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
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "direct",
                    ["protocol"] = "freedom"
                }
            },
            ["routing"] = new Dictionary<string, object?>
            {
                ["rules"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new[] { "local-http", "local-socks" },
                        ["outboundTag"] = "direct"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }
}

public sealed record XrayConfigRequest(
    string ListenHost,
    int HttpPort,
    int SocksPort,
    string AccessLogPath,
    string ErrorLogPath,
    string? ProxyUsername,
    string? ProxyPassword)
{
    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ProxyUsername) && !string.IsNullOrWhiteSpace(ProxyPassword);
}
