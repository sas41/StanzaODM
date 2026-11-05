using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stanza.ControlPlane.Persistence;

public static class ExecutionResilienceUpgrade
{
    public static async Task ApplyAsync(StanzaDbContext db, CancellationToken ct)
    {
        foreach (var column in new[] { (Table: "AutomationRuns", Name: "BlockingRunId", Type: db.Database.IsNpgsql() ? "uuid" : "TEXT"),
            (Table: "Operations", Name: "CancellationReason", Type: "text") })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = db.Database.IsNpgsql()
                ? $"SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{column.Table}' AND column_name = '{column.Name}'"
                : $"SELECT name FROM pragma_table_info('{column.Table}') WHERE name = '{column.Name}'";
            if (await command.ExecuteScalarAsync(ct) is null)
            {
                var sql = $"ALTER TABLE \"{column.Table}\" ADD COLUMN \"{column.Name}\" {column.Type} NULL";
                await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
        }
        var id = db.Database.IsNpgsql() ? "uuid" : "TEXT";
        var create = $"""
            CREATE TABLE IF NOT EXISTS "AgentDisconnects" (
                "Id" {id} NOT NULL PRIMARY KEY, "AgentId" {id} NOT NULL, "OperationId" {id} NOT NULL,
                "At" bigint NOT NULL, "Reason" text NOT NULL, "CloseStatus" integer NULL,
                "Phase" text NOT NULL, "LastHeartbeat" bigint NOT NULL, "AcknowledgedSequence" bigint NOT NULL)
            """;
        await db.Database.ExecuteSqlRawAsync(create, ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_AgentDisconnects_OperationId\" ON \"AgentDisconnects\" (\"OperationId\")", ct);
        // Backfill existing blocked runs without altering their outcomes or active locks.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "AutomationRuns" SET "BlockingRunId" = (SELECT "ActiveRunId" FROM "AutomationActions" a WHERE a."Id" = "AutomationRuns"."ActionId")
            WHERE "Code" = 'action-concurrency' AND "BlockingRunId" IS NULL
            """, ct);
    }
}
