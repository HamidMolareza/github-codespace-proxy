using GhProxy.Api.Data;
using GhProxy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Tests;

public sealed class DatabaseSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesOperationalEventsTableIdempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gh-proxy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDb(databasePath);
            var initializer = new DatabaseSchemaInitializer(db);

            await initializer.InitializeAsync(CancellationToken.None);
            await initializer.InitializeAsync(CancellationToken.None);

            var tables = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'OperationalEvents'")
                .ToListAsync();
            Assert.Single(tables);
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
