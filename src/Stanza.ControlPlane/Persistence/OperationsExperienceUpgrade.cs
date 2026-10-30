using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stanza.ControlPlane.Persistence;

public static class OperationsExperienceUpgrade
{
    public static async Task ApplyAsync(StanzaDbContext db, CancellationToken ct)
    {
        foreach (var table in new[] { "Operations", "AutomationRuns" })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = db.Database.IsNpgsql()
                ? $"SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = 'StartedAt'"
                : $"SELECT name FROM pragma_table_info('{table}') WHERE name = 'StartedAt'";
            if (await command.ExecuteScalarAsync(ct) is null)
            {
                var alter = $"ALTER TABLE \"{table}\" ADD COLUMN \"StartedAt\" bigint NULL";
                await db.Database.ExecuteSqlRawAsync(alter, ct);
            }
        }
        var id = db.Database.IsNpgsql() ? "uuid" : "TEXT";
        var locks = $"""
            CREATE TABLE IF NOT EXISTS "DeploymentLocks" (
                "DeploymentId" {id} NOT NULL PRIMARY KEY,
                "LockedBy" {id} NOT NULL,
                "Username" text NOT NULL,
                "Description" text NOT NULL,
                "ExpiresAt" bigint NOT NULL,
                CONSTRAINT "FK_DeploymentLocks_DeploymentDrafts_DeploymentId" FOREIGN KEY ("DeploymentId") REFERENCES "DeploymentDrafts" ("Id") ON DELETE CASCADE
            )
            """;
        await db.Database.ExecuteSqlRawAsync(locks, ct);
        var deliveries = $"""
            CREATE TABLE IF NOT EXISTS "OutgoingDeliveries" (
                "Id" {id} NOT NULL PRIMARY KEY,
                "ChannelId" {id} NOT NULL,
                "RunId" {id} NOT NULL,
                "Event" text NOT NULL,
                "State" text NOT NULL,
                "Attempts" integer NOT NULL,
                "NextAttemptAt" bigint NOT NULL,
                "Result" text NOT NULL,
                "ProtectedBody" text NOT NULL
            )
            """;
        await db.Database.ExecuteSqlRawAsync(deliveries, ct);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_OutgoingDeliveries_ChannelId_RunId_Event\" ON \"OutgoingDeliveries\" (\"ChannelId\", \"RunId\", \"Event\")", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_OutgoingDeliveries_State_NextAttemptAt\" ON \"OutgoingDeliveries\" (\"State\", \"NextAttemptAt\")", ct);
        // New policy properties deserialize to their defaults; don't rewrite customized policy JSON.
    }
}
