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
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "GitHubAccounts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_GitHubAccounts" PRIMARY KEY,
                "DisplayName" TEXT NOT NULL,
                "Username" TEXT NOT NULL,
                "ProtectedPersonalAccessToken" TEXT NOT NULL,
                "Plan" TEXT NOT NULL,
                "ValidationStatus" TEXT NOT NULL,
                "QuotaState" TEXT NOT NULL,
                "ValidationMessage" TEXT NULL,
                "LastError" TEXT NULL,
                "LastValidatedAt" TEXT NULL,
                "LastSyncedAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GitHubAccounts_Username" ON "GitHubAccounts" ("Username");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "CodespaceSnapshots" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CodespaceSnapshots" PRIMARY KEY,
                "AccountId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "RepositoryFullName" TEXT NULL,
                "MachineDisplayName" TEXT NULL,
                "Location" TEXT NULL,
                "WebUrl" TEXT NULL,
                "BillableOwner" TEXT NULL,
                "CreatedAt" TEXT NULL,
                "UpdatedAt" TEXT NULL,
                "LastUsedAt" TEXT NULL,
                "LastSyncedAt" TEXT NOT NULL,
                CONSTRAINT "FK_CodespaceSnapshots_GitHubAccounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "GitHubAccounts" ("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CodespaceSnapshots_AccountId_Name" ON "CodespaceSnapshots" ("AccountId", "Name");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "LocalProxyProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LocalProxyProfiles" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "BindHost" TEXT NOT NULL,
                "LocalPort" INTEGER NOT NULL,
                "ProxyUsername" TEXT NULL,
                "ProtectedProxyPassword" TEXT NULL,
                "IdleShutdownMinutes" INTEGER NOT NULL,
                "Notes" TEXT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocalProxyProfiles_Name" ON "LocalProxyProfiles" ("Name");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "LocalProxySessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LocalProxySessions" PRIMARY KEY,
                "ProfileId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "BindHost" TEXT NOT NULL,
                "LocalPort" INTEGER NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "LastActivityAt" TEXT NOT NULL,
                "StoppedAt" TEXT NULL,
                "LastError" TEXT NULL,
                "TotalRequests" INTEGER NOT NULL,
                "TotalConnectTunnels" INTEGER NOT NULL,
                "TotalBytesReceived" INTEGER NOT NULL,
                "TotalBytesSent" INTEGER NOT NULL,
                CONSTRAINT "FK_LocalProxySessions_LocalProxyProfiles_ProfileId" FOREIGN KEY ("ProfileId") REFERENCES "LocalProxyProfiles" ("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_LocalProxySessions_ProfileId" ON "LocalProxySessions" ("ProfileId");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_LocalProxySessions_StartedAt" ON "LocalProxySessions" ("StartedAt");""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LocalProxyProfiles"
            SET "Status" = 'Stopped'
            WHERE "Status" IN ('Starting', 'Running');
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "LocalProxySessions"
            SET "Status" = 'Error',
                "StoppedAt" = COALESCE("StoppedAt", CURRENT_TIMESTAMP),
                "LastError" = COALESCE("LastError", 'The application restarted while this in-memory local proxy session was active.')
            WHERE "Status" IN ('Starting', 'Running', 'Stopping');
            """,
            cancellationToken);
    }
}
