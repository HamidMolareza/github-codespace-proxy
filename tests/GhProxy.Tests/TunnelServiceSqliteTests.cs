using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class TunnelServiceSqliteTests
{
    [Fact]
    public async Task GetActiveAsync_OrdersDateTimeOffsetInMemoryForSqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            await new DatabaseSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            var node = new VpsNode
            {
                Name = "node",
                Host = "127.0.0.1",
                SshUsername = "root",
                SshKeyPath = "/tmp/key",
                ProxyUsername = "proxy",
                ProtectedProxyPassword = "secret",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.VpsNodes.Add(node);
            var olderStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var newerStartedAt = DateTimeOffset.UtcNow;
            db.ProxySessions.AddRange(
                new ProxySession
                {
                    NodeId = node.Id,
                    Status = ProxySessionStatus.Running,
                    StartedAt = olderStartedAt,
                    LastActivityAt = olderStartedAt
                },
                new ProxySession
                {
                    NodeId = node.Id,
                    Status = ProxySessionStatus.Running,
                    StartedAt = newerStartedAt,
                    LastActivityAt = newerStartedAt
                });
            await db.SaveChangesAsync();
            var service = new TunnelService(db, null!, null!, null!, null!, null!);

            var active = await service.GetActiveAsync(CancellationToken.None);

            Assert.NotNull(active);
            Assert.True(active.StartedAt > olderStartedAt);
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
