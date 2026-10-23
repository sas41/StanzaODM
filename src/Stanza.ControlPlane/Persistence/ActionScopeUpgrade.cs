using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stanza.ControlPlane.Automation;

namespace Stanza.ControlPlane.Persistence;

public static class ActionScopeUpgrade
{
    public static async Task ApplyAsync(StanzaDbContext db, IDataProtectionProvider? protection, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = db.Database.IsNpgsql()
                ? "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'AutomationActions'"
                : "SELECT name FROM pragma_table_info('AutomationActions')";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
        }
        if (!columns.Contains("ScopeDeploymentId"))
        {
            var sql = db.Database.IsNpgsql() ? "ALTER TABLE \"AutomationActions\" ADD COLUMN \"ScopeDeploymentId\" uuid NULL" : "ALTER TABLE \"AutomationActions\" ADD COLUMN \"ScopeDeploymentId\" TEXT NULL";
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        if (!columns.Contains("NormalizedName")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationActions\" ADD COLUMN \"NormalizedName\" text NOT NULL DEFAULT ''", ct);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"UX_Action_GlobalName\"", ct);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"UX_Action_ScopedName\"", ct);
        var actions = await db.Set<ActionDefinition>().OrderBy(x => x.Id).ToListAsync(ct);
        if (actions.Count > 0 && protection is null) throw new InvalidOperationException("Action scope migration requires the existing instance encryption keys.");
        var protector = protection?.CreateProtector("Stanza.Automation.v1");
        foreach (var action in actions)
        {
            var plan = JsonSerializer.Deserialize<ActionPlan>(protector!.Unprotect(action.ProtectedSteps), AutomationRules.Json)
                ?? throw new InvalidOperationException("An existing action plan could not be read. Restore its instance encryption keys before upgrading.");
            action.ScopeDeploymentId = plan.DeploymentId;
            // Preserve invalid legacy steps for editing, but never execute them under a deployment scope.
            if (!AutomationRules.MatchesScope(plan) && action.Enabled) { action.Enabled = false; action.Revision++; }
        }
        foreach (var scope in actions.GroupBy(x => x.ScopeDeploymentId))
        {
            var reserved = scope.Select(x => AutomationRules.NormalizeName(x.Name)).ToHashSet(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in scope.OrderBy(x => x.Id))
            {
                var name = action.Name.Trim();
                var normalized = AutomationRules.NormalizeName(name);
                if (!used.Add(normalized))
                {
                    for (var suffix = 2; ; suffix++)
                    {
                        var ending = $" ({suffix})";
                        var candidate = name[..Math.Min(name.Length, 100 - ending.Length)] + ending;
                        normalized = AutomationRules.NormalizeName(candidate);
                        if (reserved.Contains(normalized) || !used.Add(normalized)) continue;
                        name = candidate; break;
                    }
                }
                action.Name = name; action.NormalizedName = normalized;
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var action in actions) db.Entry(action).State = EntityState.Detached;
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX \"UX_Action_GlobalName\" ON \"AutomationActions\" (\"NormalizedName\") WHERE \"ScopeDeploymentId\" IS NULL", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX \"UX_Action_ScopedName\" ON \"AutomationActions\" (\"ScopeDeploymentId\", \"NormalizedName\") WHERE \"ScopeDeploymentId\" IS NOT NULL", ct);
    }
}
