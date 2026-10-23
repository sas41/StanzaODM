using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class ActionScopeTests
{
    [Fact]
    public async Task NamesAreCaseInsensitiveAndUniqueOnlyWithinTheirScope()
    {
        await using var s = await AutomationTestStore.Create();
        var global = await s.Service.SaveActionAsync(s.Root, null, 0, "Deploy", true, "queue", new([new("deploy", s.Deployment)]));
        var local = await s.Service.SaveActionAsync(s.Root, null, 0, "Deploy", true, "queue", new([new("deploy", s.Deployment)], DeploymentId: s.Deployment));
        var other = await s.Service.SaveActionAsync(s.Root, null, 0, "Deploy", true, "queue", new([new("deploy", s.OtherDeployment)], DeploymentId: s.OtherDeployment));
        Assert.Null(global.ScopeDeploymentId); Assert.Equal(s.Deployment, local.ScopeDeploymentId); Assert.Equal(s.OtherDeployment, other.ScopeDeploymentId);
        Assert.Equal("DEPLOY", local.NormalizedName);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionAsync(s.Root, null, 0, " deploy ", false, "reject", new([new("restart", s.Deployment)])));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionAsync(s.Root, null, 0, "DEPLOY", false, "queue", new([new("restart", s.Deployment)], DeploymentId: s.Deployment)));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionAsync(s.Root, local.Id, local.Revision, "Deploy", true, "queue", new([new("deploy", s.Deployment)])));
        Assert.Equal(s.Deployment, (await s.Service.ReadActionAsync(s.Root, local.Id)).Definition.ScopeDeploymentId);
        var saved = await s.Service.SaveActionAsync(s.Root, local.Id, local.Revision, "DEPLOY", false, "queue", new([new("restart", s.Deployment)], DeploymentId: s.Deployment));
        Assert.Equal(2, saved.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedActionsCannotContainOtherDeploymentOrHostStepsEvenViaAdvancedJson(bool host)
    {
        await using var s = await AutomationTestStore.Create();
        var escaped = host ? new ActionStep("host-exec", AgentId: Guid.NewGuid(), Execution: new("/usr/bin/true", [])) : new("deploy", s.OtherDeployment);
        var plan = new ActionPlan([new("deploy", s.Deployment), escaped], DeploymentId: s.Deployment);
        var json = System.Text.Json.JsonSerializer.Serialize(plan, AutomationRules.Json);
        Assert.Throws<AutomationException>(() => AutomationRules.ParsePlan(json));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionAsync(s.Root, null, 0, "Scoped escape", true, "queue", plan));
        Assert.Empty(await s.Db.Set<ActionDefinition>().ToListAsync());
        var global = await s.Service.SaveActionAsync(s.Root, null, 0, "Global workflow", true, "queue", plan with { DeploymentId = null });
        Assert.Null(global.ScopeDeploymentId);
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateScopeKeysAndScopeDoesNotGrantActionPermissions()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Service.SaveActionAsync(s.Root, null, 0, "Inspect", true, "queue", new([new("status", s.Deployment)], DeploymentId: s.Deployment));
        await s.Grant("deployment.read", "deployment", s.Deployment);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ReadActionAsync(s.User, action.Id));
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.SaveActionAsync(s.User, action.Id, 1, "Changed", false, "queue", new([new("status", s.Deployment)], DeploymentId: s.Deployment)));
        s.Db.Add(new ActionDefinition { Id = Guid.NewGuid(), Name = "inspect", NormalizedName = "INSPECT", ScopeDeploymentId = s.Deployment });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task ScopeRulesAreRecheckedBeforeQueuingLegacyOrTamperedDefinitions()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Service.SaveActionAsync(s.Root, null, 0, "Scoped", true, "queue", new([new("deploy", s.Deployment)], DeploymentId: s.Deployment));
        var escaped = s.Service.Protect(new ActionPlan([new("deploy", s.OtherDeployment)], DeploymentId: s.Deployment));
        await s.Db.Set<ActionDefinition>().Where(x => x.Id == action.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedSteps, escaped));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.Root, action.Id));
        Assert.Empty(await s.Db.Set<AutomationRun>().ToListAsync());
    }

    [Fact]
    public async Task MovingAnActionIntoADeploymentScopeRevokesOldGlobalRunsBeforeDispatch()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Service.SaveActionAsync(s.Root, null, 0, "Workflow", true, "queue", new([new("deploy", s.OtherDeployment)]));
        var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        await s.Db.Set<AutomationRun>().Where(x => x.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "running").SetProperty(x => x.LeaseUntil, s.Service.Now + 120));
        await s.Service.SaveActionAsync(s.Root, action.Id, 1, "Workflow", true, "queue", new([new("deploy", s.Deployment)], DeploymentId: s.Deployment));
        var guard = new AutomationOperationGuard(s.Db, s.Service, s.Authorization);
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.AuthorizeAsync(s.Root, "automation:" + run.Id.ToString("N"), "deploy", s.OtherDeployment, Guid.NewGuid()));
    }
}
