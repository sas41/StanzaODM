using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stanza.ControlPlane.Persistence;

public static class ExecutionConcurrencyUpgrade
{
    public static async Task ApplyAsync(StanzaDbContext db, CancellationToken ct)
    {
        var id = db.Database.IsNpgsql() ? "uuid" : "TEXT";
        var create = $"""
            CREATE TABLE IF NOT EXISTS "DeploymentExecutionReservations" (
                "DeploymentId" {id} NOT NULL PRIMARY KEY, "RunId" {id} NOT NULL)
            """;
        await db.Database.ExecuteSqlRawAsync(create, ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_DeploymentExecutionReservations_RunId\" ON \"DeploymentExecutionReservations\" (\"RunId\")", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Operations_AgentId_State\" ON \"Operations\" (\"AgentId\", \"State\")", ct);
        await using var column = db.Database.GetDbConnection().CreateCommand();
        column.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        column.CommandText = db.Database.IsNpgsql()
            ? "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'AutomationSchedules' AND column_name = 'PendingOccurrenceAt'"
            : "SELECT name FROM pragma_table_info('AutomationSchedules') WHERE name = 'PendingOccurrenceAt'";
        if (await column.ExecuteScalarAsync(ct) is null)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationSchedules\" ADD COLUMN \"PendingOccurrenceAt\" bigint NULL", ct);
        // Missing Agent policy fields inherit constructor defaults. Preserve existing settings,
        // encrypted snapshots, managed files and history without rewriting them.
    }
}
