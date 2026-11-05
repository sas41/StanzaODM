using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class ExecutionResilienceUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndVersionThreeUpgradesAreRepeatableAndPreserveSettingsResultsAndUnresolvedLocks(bool existing)
    {
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        if (!existing) await DatabaseUpgrader.UpgradeAsync(db);
        var action = Guid.NewGuid(); var active = Guid.NewGuid(); var blocked = Guid.NewGuid(); var operation = Guid.NewGuid();
        const string customPolicy = "{\"Policy\":{\"LogRetentionDays\":42,\"OperationTimeoutSeconds\":0},\"Version\":7}";
        db.Settings.Add(new() { Key = "operational-policy", Value = customPolicy });
        db.Add(new ActionDefinition { Id = action, ActiveRunId = active, Name = "custom", NormalizedName = "CUSTOM" });
        db.AddRange(new AutomationRun { Id = active, ActionId = action, State = "uncertain", OccurrenceKey = "active" },
            new AutomationRun { Id = blocked, ActionId = action, State = "skipped", Code = "action-concurrency", OccurrenceKey = "blocked", BlockingRunId = existing ? null : active });
        db.Add(new OperationEntity { Id = operation, State = "Unknown", ProtectedRequest = "old-private-snapshot", ProtectedResponse = "old-private-result" });
        await db.SaveChangesAsync();
        if (existing)
        {
            db.Settings.Add(new() { Key = "schema-version", Value = "3" }); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AgentDisconnects\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Operations\" DROP COLUMN \"CancellationReason\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationRuns\" DROP COLUMN \"BlockingRunId\"");
        }
        db.ChangeTracker.Clear();
        await DatabaseUpgrader.UpgradeAsync(db); await DatabaseUpgrader.UpgradeAsync(db);
        Assert.Equal(customPolicy, (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "operational-policy")).Value);
        Assert.Equal(DatabaseUpgrader.CurrentVersion.ToString(), (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
        Assert.Equal(active, (await db.Set<ActionDefinition>().SingleAsync()).ActiveRunId);
        Assert.Equal(active, (await db.Set<AutomationRun>().SingleAsync(x => x.Id == blocked)).BlockingRunId);
        Assert.Equal("uncertain", (await db.Set<AutomationRun>().SingleAsync(x => x.Id == active)).State);
        var saved = await db.Set<OperationEntity>().SingleAsync();
        Assert.Equal("Unknown", saved.State); Assert.Equal("old-private-result", saved.ProtectedResponse); Assert.Equal("old-private-snapshot", saved.ProtectedRequest);
        db.Add(new AgentDisconnectEntity { Id = Guid.NewGuid(), OperationId = operation, Reason = "WebSocketException", Phase = "Building" });
        await db.SaveChangesAsync(); Assert.Single(await db.Set<AgentDisconnectEntity>().ToListAsync());
    }
}
