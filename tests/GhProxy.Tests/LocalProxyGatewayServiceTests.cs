using System.Text;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class LocalProxyGatewayServiceTests
{
    [Fact]
    public void TryParseHttpTarget_ParsesConnectHostWithoutPath()
    {
        var payload = Encoding.ASCII.GetBytes("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");

        var parsed = LocalProxyGatewayService.TryParseHttpTarget(payload, out var target);

        Assert.True(parsed);
        Assert.Equal("example.com", target.Host);
        Assert.Equal(443, target.Port);
    }

    [Fact]
    public void TryParseHttpTarget_ParsesHostHeaderWithoutRequestPath()
    {
        var payload = Encoding.ASCII.GetBytes("GET /secret-path?token=abc HTTP/1.1\r\nHost: api.example.com:8443\r\n\r\n");

        var parsed = LocalProxyGatewayService.TryParseHttpTarget(payload, out var target);

        Assert.True(parsed);
        Assert.Equal("api.example.com", target.Host);
        Assert.Equal(8443, target.Port);
    }

    [Fact]
    public void TryParseSocksTarget_ParsesDomainRequest()
    {
        var host = Encoding.ASCII.GetBytes("github.com");
        var payload = new byte[2 + 1 + 4 + 1 + host.Length + 2];
        payload[0] = 0x05;
        payload[1] = 0x01;
        payload[2] = 0x00;
        var offset = 3;
        payload[offset] = 0x05;
        payload[offset + 1] = 0x01;
        payload[offset + 2] = 0x00;
        payload[offset + 3] = 0x03;
        payload[offset + 4] = (byte)host.Length;
        host.CopyTo(payload.AsMemory(offset + 5));
        payload[^2] = 0x01;
        payload[^1] = 0xbb;

        var parsed = LocalProxyGatewayService.TryParseSocksTarget(payload, out var target);

        Assert.True(parsed);
        Assert.Equal("github.com", target.Host);
        Assert.Equal(443, target.Port);
    }

    [Fact]
    public async Task TrimRequestHistoryAsync_KeepsNewestRequests()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var start = new DateTimeOffset(2026, 5, 22, 6, 0, 0, TimeSpan.Zero);
            db.LocalProxyGatewayRequests.AddRange(Enumerable.Range(0, 6).Select(index => new LocalProxyGatewayRequest
            {
                ObservedAt = start.AddMinutes(index),
                Protocol = "HTTP",
                Outcome = "Forwarded",
                TargetHost = $"host-{index}.test"
            }));
            await db.SaveChangesAsync();

            await LocalProxyGatewayService.TrimRequestHistoryAsync(db, 3, CancellationToken.None);

            var remaining = await db.LocalProxyGatewayRequests
                .AsNoTracking()
                .ToListAsync();
            var hosts = remaining.OrderBy(x => x.ObservedAt).Select(x => x.TargetHost).ToList();
            Assert.Equal(["host-3.test", "host-4.test", "host-5.test"], hosts);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static AppDbContext CreateDb(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new AppDbContext(options);
    }
}
