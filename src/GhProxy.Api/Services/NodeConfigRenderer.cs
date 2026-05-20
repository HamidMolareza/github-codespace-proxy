using System.Text;
using GhProxy.Api.Domain;

namespace GhProxy.Api.Services;

public sealed class NodeConfigRenderer(ISecretProtector secretProtector)
{
    public string RenderCompose(VpsNode node)
    {
        return $$"""
services:
  gh-proxy:
    image: 3proxy/3proxy:latest
    container_name: gh-proxy
    restart: unless-stopped
    ports:
      - "127.0.0.1:{{node.RemoteHttpPort}}:{{node.RemoteHttpPort}}"
      - "127.0.0.1:{{node.RemoteSocksPort}}:{{node.RemoteSocksPort}}"
    volumes:
      - ./3proxy.cfg:/etc/3proxy/3proxy.cfg:ro
""";
    }

    public string RenderProxyConfig(VpsNode node)
    {
        var password = secretProtector.Unprotect(node.ProtectedProxyPassword);
        var builder = new StringBuilder();
        builder.AppendLine("daemon");
        builder.AppendLine("nscache 65536");
        builder.AppendLine("timeouts 1 5 30 60 180 1800 15 60");
        builder.AppendLine("auth strong");
        builder.AppendLine($"users {Escape(node.ProxyUsername)}:CL:{Escape(password)}");
        builder.AppendLine("allow *");
        builder.AppendLine($"proxy -n -a -p{node.RemoteHttpPort} -i0.0.0.0 -e0.0.0.0");
        builder.AppendLine($"socks -p{node.RemoteSocksPort} -i0.0.0.0 -e0.0.0.0");
        builder.AppendLine("flush");
        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace(" ", "_", StringComparison.Ordinal);
}
