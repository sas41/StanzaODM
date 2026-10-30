using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class OperationsExperienceUpgradeTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task FreshAndVersionTwoDatabasesUpgradeIdempotentlyWithoutChangingDataOrCustomizedSettings(bool legacy)
    {
        await using var db = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var id = Guid.NewGuid(); var run = Guid.NewGuid();
        var policy = JsonSerializer.Serialize(new PolicyEditor(new() { OperationTimeoutSeconds = 19, MaximumParallelOperations = 7 }, 11));
        var node = System.Text.Json.Nodes.JsonNode.Parse(policy)!; node["Policy"]!.AsObject().Remove("MaximumDeploymentLockHours"); policy = node.ToJsonString();
        db.Settings.AddRange(new SettingEntity { Key = "schema-version", Value = "2" }, new SettingEntity { Key = "operational-policy", Value = policy });
        db.Add(new OperationEntity { Id = id, State = "Succeeded", ProtectedRequest = "encrypted-snapshot", CompletedAt = 123 });
        db.Add(new AutomationRun { Id = run, State = "queued", OccurrenceKey = "test", ProtectedPlan = "encrypted-plan" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (legacy)
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"DeploymentLocks\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"OutgoingDeliveries\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Operations\" DROP COLUMN \"StartedAt\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationRuns\" DROP COLUMN \"StartedAt\"");
        }
        await DatabaseUpgrader.UpgradeAsync(db); await DatabaseUpgrader.UpgradeAsync(db);
        var operation = await db.Set<OperationEntity>().SingleAsync(); Assert.Equal(id, operation.Id); Assert.Null(operation.StartedAt); Assert.Equal("encrypted-snapshot", operation.ProtectedRequest);
        Assert.Equal("encrypted-plan", (await db.Set<AutomationRun>().SingleAsync()).ProtectedPlan);
        Assert.Empty(await db.Set<DeploymentLock>().ToListAsync()); Assert.Empty(await db.Set<OutgoingDelivery>().ToListAsync());
        Assert.Equal(policy, (await db.Settings.SingleAsync(x => x.Key == "operational-policy")).Value);
        var restored = JsonSerializer.Deserialize<PolicyEditor>(policy)!; Assert.Equal(168, restored.Policy.MaximumDeploymentLockHours); Assert.Equal(7, restored.Policy.MaximumParallelOperations);
        Assert.Equal(DatabaseUpgrader.CurrentVersion.ToString(), (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
    }
}
