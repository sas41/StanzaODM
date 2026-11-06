using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class ExecutionConcurrencyUpgradeTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FreshAndVersionFourInstallationsGetReservationsAndSafeDefaultsWithoutRewritingCustomizedData(bool upgrade)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); var agent = await fixture.AgentAsync(); await using var db = fixture.Context();
        const string agentJson = "{\"ExecutionEnabled\":false,\"TimeoutSeconds\":7,\"DockerEndpoint\":\"unix:///run/custom.sock\"}";
        db.Add(new AgentPolicyEntity { AgentId = agent, Version = 11, Json = agentJson });
        var policy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new PolicyEditor(new() { OperationTimeoutSeconds = 19 }, 9)))!;
        policy["Policy"]!["MaximumParallelOperations"] = 7; var globalJson = policy.ToJsonString();
        db.Settings.Add(new() { Key = "operational-policy", Value = globalJson });
        db.Settings.Add(new() { Key = "schema-version", Value = upgrade ? "4" : "0" });
        var automation = new AutomationService(db, new(fixture.Access(db)), fixture.Protection, fixture.Clock);
        var action = new ActionDefinition { Id = Guid.NewGuid(), OwnerId = fixture.Owner, Name = "Custom", NormalizedName = "CUSTOM", ProtectedSteps = automation.Protect(new ActionPlan([new("deploy", Guid.NewGuid())])) };
        db.Add(action); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (upgrade)
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"DeploymentExecutionReservations\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationSchedules\" DROP COLUMN \"PendingOccurrenceAt\"");
        }
        await DatabaseUpgrader.UpgradeAsync(db, protection: fixture.Protection); await DatabaseUpgrader.UpgradeAsync(db, protection: fixture.Protection);
        Assert.Empty(await db.Set<DeploymentExecutionReservation>().ToArrayAsync());
        Assert.Empty(await db.Set<AutomationSchedule>().ToArrayAsync());
        Assert.Equal(agentJson, (await db.Set<AgentPolicyEntity>().AsNoTracking().SingleAsync()).Json);
        Assert.Equal(11, (await db.Set<AgentPolicyEntity>().AsNoTracking().SingleAsync()).Version);
        Assert.Equal(action.ProtectedSteps, (await db.Set<ActionDefinition>().AsNoTracking().SingleAsync()).ProtectedSteps);
        var runtime = await fixture.AgentPolicies(db).RuntimeAsync(agent);
        Assert.False(runtime.ExecutionEnabled); Assert.Equal("unix:///run/custom.sock", runtime.DockerEndpoint); Assert.Equal(19, runtime.TimeoutSeconds);
        Assert.Equal(1, runtime.MaximumParallelOperations); Assert.Equal(4, runtime.MaximumParallelDiscovery); Assert.Equal(60, runtime.DiscoveryTimeoutSeconds);
        Assert.Equal(globalJson, (await db.Settings.SingleAsync(x => x.Key == "operational-policy")).Value);
        Assert.Equal("5", (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
    }
}
