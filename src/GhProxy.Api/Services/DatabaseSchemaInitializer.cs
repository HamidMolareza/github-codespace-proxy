using GhProxy.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class DatabaseSchemaInitializer(AppDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "OperationalEvents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_OperationalEvents" PRIMARY KEY,
                "Timestamp" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "EventType" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "NodeId" TEXT NULL,
                "SessionId" TEXT NULL,
                "CorrelationId" TEXT NULL,
                "CommandKind" TEXT NULL,
                "CommandDisplay" TEXT NULL,
                "ExitCode" INTEGER NULL,
                "DurationMs" INTEGER NULL,
                "TimedOut" INTEGER NOT NULL,
                "StandardOutputSnippet" TEXT NULL,
                "StandardErrorSnippet" TEXT NULL,
                "DetailsJson" TEXT NULL
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_OperationalEvents_Timestamp" ON "OperationalEvents" ("Timestamp");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_OperationalEvents_CorrelationId" ON "OperationalEvents" ("CorrelationId");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_OperationalEvents_NodeId" ON "OperationalEvents" ("NodeId");""",
            cancellationToken);
    }
}
