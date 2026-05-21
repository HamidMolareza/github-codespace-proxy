using GhProxy.Api.Services;

namespace GhProxy.Tests;

public sealed class DirectNetworkEnvironmentTests
{
    [Fact]
    public void CreateGitHubCommandEnvironment_ClearsProxyVariablesAndSetsGitHubToken()
    {
        var environment = DirectNetworkEnvironment.CreateGitHubCommandEnvironment("secret-token");

        Assert.Equal("secret-token", environment["GITHUB_TOKEN"]);
        Assert.Equal("secret-token", environment["GH_TOKEN"]);
        Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
        Assert.Equal("1", environment["GH_NO_UPDATE_NOTIFIER"]);
        Assert.Equal("*", environment["NO_PROXY"]);
        Assert.Equal("*", environment["no_proxy"]);
        Assert.Null(environment["HTTP_PROXY"]);
        Assert.Null(environment["http_proxy"]);
        Assert.Null(environment["HTTPS_PROXY"]);
        Assert.Null(environment["https_proxy"]);
        Assert.Null(environment["ALL_PROXY"]);
        Assert.Null(environment["all_proxy"]);
        Assert.Null(environment["GIT_PROXY_COMMAND"]);
        Assert.Null(environment["GIT_HTTP_PROXY"]);
        Assert.Null(environment["GIT_HTTPS_PROXY"]);
    }

    [Fact]
    public void CreateHttpClientHandler_DisablesProxyUse()
    {
        using var handler = DirectNetworkEnvironment.CreateHttpClientHandler();

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }
}
