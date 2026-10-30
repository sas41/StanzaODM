using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Operations;
using Stanza.Tests.Automation;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentLockTests
{
    [Fact]
    public async Task OwnerAndAdministratorCanUnlockButOtherDeployersCannotReplaceOrReleaseTheLock()
    {
        await using var s = await AutomationTestStore.Create();
        s.Db.Add(new DeploymentDraftEntity { Id = s.Deployment, Name = "App" }); await s.Db.SaveChangesAsync();
        var locks = new DeploymentLockService(s.Db, s.Access, new(s.Db, s.Access), s.Clock);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => locks.LockAsync(s.User, s.Deployment, 1, null));
        await s.Grant("deployment.read", "deployment", s.Deployment); await s.Grant("deployment.deploy", "deployment", s.Deployment);
        await locks.LockAsync(s.Root, s.Deployment, 168, "Maintenance");
        var view = await locks.ReadAsync(s.User, s.Deployment);
        Assert.Equal("root", view.Current!.Username); Assert.Equal("Maintenance", view.Current.Description); Assert.False(view.CanUnlock);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => locks.LockAsync(s.User, s.Deployment, 1, "overwrite"));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => locks.UnlockAsync(s.User, s.Deployment));
        await locks.UnlockAsync(s.Root, s.Deployment);
        s.Db.ChangeTracker.Clear();
        await locks.LockAsync(s.User, s.Deployment, 2, null);
        Assert.True((await locks.ReadAsync(s.User, s.Deployment)).CanUnlock);
        await locks.UnlockAsync(s.Root, s.Deployment); // administrator releases another user's lock
        Assert.Null((await locks.ReadAsync(s.User, s.Deployment)).Current);
        s.Db.ChangeTracker.Clear();
        await locks.LockAsync(s.User, s.Deployment, 1, "Owner release");
        await locks.UnlockAsync(s.User, s.Deployment);
    }

    [Fact]
    public async Task DefaultMaximumOverrideUnlimitedExpiryAndExistingLockPreservationAreEnforced()
    {
        await using var s = await AutomationTestStore.Create();
        s.Db.Add(new DeploymentDraftEntity { Id = s.Deployment }); await s.Db.SaveChangesAsync();
        var policies = new OperationalPolicyService(s.Db, s.Access);
        var locks = new DeploymentLockService(s.Db, s.Access, policies, s.Clock);
        Assert.Equal(168, (await locks.ReadAsync(s.Root, s.Deployment)).MaximumHours);
        foreach (var invalid in new[] { -1, 0, 169 }) await Assert.ThrowsAsync<DeploymentDraftException>(() => locks.LockAsync(s.Root, s.Deployment, invalid, null));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => locks.LockAsync(s.Root, s.Deployment, 1, new string('x', 1001)));
        await policies.SaveAsync(s.Root, new(new() { MaximumDeploymentLockHours = 500 }, 0));
        await locks.LockAsync(s.Root, s.Deployment, 500, "Long maintenance");
        var expiry = (await locks.ReadAsync(s.Root, s.Deployment)).Current!.ExpiresAt;
        await policies.SaveAsync(s.Root, new(new() { MaximumDeploymentLockHours = 1 }, 1));
        Assert.Equal(expiry, (await locks.ReadAsync(s.Root, s.Deployment)).Current!.ExpiresAt);
        s.Clock.Advance(TimeSpan.FromHours(500));
        Assert.Null((await locks.ReadAsync(s.Root, s.Deployment)).Current);
        Assert.False(await DeploymentLockService.IsLockedAsync(s.Db, s.Deployment, s.Service.Now));
        await policies.SaveAsync(s.Root, new(new() { MaximumDeploymentLockHours = 0 }, 2));
        await locks.LockAsync(s.Root, s.Deployment, 501, null);
    }

    [Fact]
    public async Task LocksBlockAllNewTriggersAndHoldAlreadyQueuedActionsUntilExpiryWithoutDroppingThem()
    {
        await using var s = await AutomationTestStore.Create();
        s.Db.Add(new DeploymentDraftEntity { Id = s.Deployment }); await s.Db.SaveChangesAsync();
        var locks = new DeploymentLockService(s.Db, s.Access, new(s.Db, s.Access), s.Clock);
        var action = await s.Action(new([new("restart", s.Deployment)], DeploymentId: s.Deployment));
        var queued = await s.Service.QueueActionAsync(s.Root, action.Id);
        var schedule = await s.Schedule(action.Id); s.Clock.Advance(TimeSpan.FromMinutes(1));
        await locks.LockAsync(s.Root, s.Deployment, 1, null);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.Root, action.Id));
        var hook = await s.Hook(action.Id); var body = System.Text.Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        var receipt = await s.Receiver().ReceiveAsync(hook.Definition.Id, WebhookTests.Sign(hook.Secret!, body), "locked-delivery", "push", body);
        Assert.NotEqual(202, receipt.Status); Assert.Null(receipt.RunId);
        Assert.False(await s.Executor().ClaimAsync(queued));
        await s.Scheduler().TickAsync();
        Assert.Equal("denied", (await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.DefinitionId == schedule.Id)).State);
        Assert.Equal("queued", (await s.Service.ReadRunAsync(s.Root, queued.Id)).Run.State);
        s.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await s.Executor().ExecuteNextAsync());
        Assert.Equal("succeeded", (await s.Service.ReadRunAsync(s.Root, queued.Id)).Run.State);
    }

    [Fact]
    public async Task LockedImagePoliciesAdvanceTheirScheduleAndDoNotStarveOtherPolicies()
    {
        await using var s = await AutomationTestStore.Create();
        s.Db.Add(new DeploymentDraftEntity { Id = s.Deployment }); await s.Db.SaveChangesAsync();
        var first = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Locked policy", DeploymentId = s.Deployment, Services = "app", Enabled = true, FrequencyMinutes = 1 }, 0);
        var second = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Independent policy", DeploymentId = s.OtherDeployment, Services = "app", Enabled = true, FrequencyMinutes = 1 }, 0);
        await new DeploymentLockService(s.Db, s.Access, new(s.Db, s.Access), s.Clock).LockAsync(s.Root, s.Deployment, 1, null);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var blocked = await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.DefinitionId == first.Id);
        Assert.Equal("skipped", blocked.State); Assert.Equal("deployment-locked", blocked.Code);
        Assert.Equal("queued", (await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.DefinitionId == second.Id)).State);
        Assert.True((await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync(x => x.Id == first.Id)).NextRunAt > s.Service.Now);
    }

    [Fact]
    public async Task MoreThanOneHundredLockedOperationsDoNotHideIndependentWorkFromTheWorker()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            await s.GetRequiredService<DeploymentLockService>().LockAsync(h.Root, h.Deployment, 1, null);
            var db = s.GetRequiredService<StanzaDbContext>();
            db.AddRange(Enumerable.Range(0, 101).Select(_ => new OperationEntity { Id = Guid.NewGuid(), DeploymentId = h.Deployment, AgentId = h.Agent, ActorId = h.Root, Kind = "restart" }));
            await db.SaveChangesAsync(); return true;
        });
        var other = await h.AddDeploymentAsync(); var independent = await h.EnqueueAsync(deployment: other);
        await h.StartWorkerAsync(); await h.WaitStateAsync(independent.Id, "Succeeded");
    }

    [Fact]
    public async Task LowLevelOperationsWaitForUnlockWhileOtherDeploymentsStillRun()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var queued = await h.EnqueueAsync();
        await h.InScope(async s => { await s.GetRequiredService<DeploymentLockService>().LockAsync(h.Root, h.Deployment, 1, null); return true; });
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync());
        var other = await h.AddDeploymentAsync(); var independent = await h.EnqueueAsync(deployment: other);
        await h.StartWorkerAsync(); await h.WaitStateAsync(independent.Id, "Succeeded");
        Assert.Equal("Queued", (await h.RowAsync(queued.Id)).State);
        await h.InScope(async s => { await s.GetRequiredService<DeploymentLockService>().UnlockAsync(h.Root, h.Deployment); return true; });
        await h.NextTickAsync();
        Assert.NotNull((await h.WaitStateAsync(queued.Id, "Succeeded")).StartedAt);
    }
}
