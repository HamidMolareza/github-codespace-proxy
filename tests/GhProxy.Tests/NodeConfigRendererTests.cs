using GhProxy.Api.Domain;
using GhProxy.Api.Services;

namespace GhProxy.Tests;

public sealed class NodeConfigRendererTests
{
    [Fact]
    public void RenderProxyConfig_UsesConfiguredPortsAndCredentials()
    {
        var node = new VpsNode
        {
            RemoteHttpPort = 3128,
            RemoteSocksPort = 1080,
            ProxyUsername = "proxy-user",
            ProtectedProxyPassword = "clear-password"
        };
        var renderer = new NodeConfigRenderer(new PassThroughSecretProtector());

        var config = renderer.RenderProxyConfig(node);

        Assert.Contains("proxy -n -a -p3128", config, StringComparison.Ordinal);
        Assert.Contains("socks -p1080", config, StringComparison.Ordinal);
        Assert.Contains("users proxy-user:CL:clear-password", config, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCompose_BindsProxyPortsToLoopbackOnly()
    {
        var node = new VpsNode
        {
            RemoteHttpPort = 3128,
            RemoteSocksPort = 1080
        };
        var renderer = new NodeConfigRenderer(new PassThroughSecretProtector());

        var compose = renderer.RenderCompose(node);

        Assert.Contains("\"127.0.0.1:3128:3128\"", compose, StringComparison.Ordinal);
        Assert.Contains("\"127.0.0.1:1080:1080\"", compose, StringComparison.Ordinal);
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public string Protect(string value) => value;

        public string Unprotect(string value) => value;
    }
}
