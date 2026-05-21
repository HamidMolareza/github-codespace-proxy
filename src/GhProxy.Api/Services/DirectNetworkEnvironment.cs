using System.Net;

namespace GhProxy.Api.Services;

public static class DirectNetworkEnvironment
{
    private static readonly string[] ProxyVariables =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "FTP_PROXY",
        "RSYNC_PROXY",
        "SOCKS_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "ftp_proxy",
        "rsync_proxy",
        "socks_proxy"
    ];

    private static readonly string[] GitProxyVariables =
    [
        "GIT_PROXY_COMMAND",
        "GIT_HTTP_PROXY",
        "GIT_HTTPS_PROXY",
        "git_proxy_command",
        "git_http_proxy",
        "git_https_proxy"
    ];

    public static IReadOnlyDictionary<string, string?> CreateGitHubCommandEnvironment(string token)
    {
        var environment = CreateNoProxyEnvironment();
        environment["GITHUB_TOKEN"] = token;
        environment["GH_TOKEN"] = token;
        environment["GH_PROMPT_DISABLED"] = "1";
        environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        return environment;
    }

    public static Dictionary<string, string?> CreateNoProxyEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in ProxyVariables.Concat(GitProxyVariables))
        {
            environment[variable] = null;
        }

        environment["NO_PROXY"] = "*";
        environment["no_proxy"] = "*";
        return environment;
    }

    public static SocketsHttpHandler CreateHttpClientHandler() =>
        new()
        {
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.All
        };
}
