using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Configuration;

public sealed class ActionScopeUpgradeTests
{
    [Fact]
    public async Task ExistingNamesAreSuffixedDeterministicallyAndReferencesAndEncryptedPlansSurvive()
    {
        await using var s = await ConfigurationTestStore.CreateAsync(); await using var db = s.Context();
        var automation = new AutomationService(db, new(s.Access(db)), s.Protection, s.Clock);
        var deployment = Guid.NewGuid(); var other = Guid.NewGuid();
        var plan = new ActionPlan([new("deploy", deployment)], DeploymentId: deployment);
        var records = new[]
        {
            Legacy(1, "Deploy", plan), Legacy(2, "deploy", plan), Legacy(3, "Deploy (2)", plan),
            Legacy(4, "Deploy", plan with { DeploymentId = null }),
            Legacy(5, "Deploy", new([new("deploy", other)], DeploymentId: other)),
            Legacy(6, "Unsafe old workflow", new([new("deploy", other)], DeploymentId: deployment))
        };
        ActionDefinition Legacy(int number, string name, ActionPlan p) => new() { Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), OwnerId = s.Owner,
            Name = name, NormalizedName = number.ToString(), ProtectedSteps = automation.Protect(p), Enabled = true, Revision = 3 };
        db.AddRange(records);
        var schedule = new AutomationSchedule { Id = Guid.NewGuid(), ActionId = records[1].Id, Name = "Existing schedule" };
        db.Add(schedule); db.Settings.Add(new() { Key = "schema-version", Value = "1" }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var encrypted = records.ToDictionary(x => x.Id, x => x.ProtectedSteps);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_Action_GlobalName\"");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_Action_ScopedName\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationActions\" DROP COLUMN \"ScopeDeploymentId\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationActions\" DROP COLUMN \"NormalizedName\"");
        await DatabaseUpgrader.UpgradeAsync(db, protection: s.Protection);
        await DatabaseUpgrader.UpgradeAsync(db, protection: s.Protection);
        var saved = await db.Set<ActionDefinition>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(new[] { "Deploy", "deploy (3)", "Deploy (2)", "Deploy", "Deploy", "Unsafe old workflow" }, saved.Select(x => x.Name));
        Assert.Equal(deployment, saved[0].ScopeDeploymentId); Assert.Null(saved[3].ScopeDeploymentId); Assert.Equal(other, saved[4].ScopeDeploymentId);
        Assert.False(saved[5].Enabled); Assert.True(saved[0].Enabled);
        Assert.All(saved, x => Assert.Equal(encrypted[x.Id], x.ProtectedSteps));
        Assert.Equal(records[1].Id, (await db.Set<AutomationSchedule>().SingleAsync()).ActionId);
        Assert.Equal("2", (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
    }

    [Fact]
    public async Task MissingEncryptionKeysRollBackUpgradeWithoutDiscardingActionData()
    {
        await using var s = await ConfigurationTestStore.CreateAsync(); await using var db = s.Context();
        db.Add(new ActionDefinition { Id = Guid.NewGuid(), Name = "Saved", NormalizedName = "SAVED", ProtectedSteps = "existing-encrypted-plan" });
        db.Settings.Add(new() { Key = "schema-version", Value = "1" }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseUpgrader.UpgradeAsync(db));
        Assert.Equal("existing-encrypted-plan", (await db.Set<ActionDefinition>().AsNoTracking().SingleAsync()).ProtectedSteps);
        Assert.Equal("1", (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "schema-version")).Value);
    }
}
