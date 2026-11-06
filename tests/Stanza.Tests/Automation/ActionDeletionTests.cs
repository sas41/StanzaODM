using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Automation;

public sealed class ActionDeletionTests
{
    private static ActionDeletionService Deletion(AutomationTestStore s) => new(s.Db, s.Service, s.Authorization);

    [Fact]
    public async Task QueueResolvingInputsOutsideTheTransactionCannotEnqueueAfterDeletion()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, ct) =>
        {
            if (request.Operation == "git-action-branches") return new(request.Id, true, "ok", "[\"main\"]");
            Assert.Equal("resolve-action-inputs", request.Operation);
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = new('a', 40) });
        };
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue",
            new([new("deploy", h.Deployment)], [new("BRANCH", Input: "branch", Source: "sources/app")], h.Deployment)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var queue = h.InScope(s => s.GetRequiredService<AutomationService>().QueueActionAsync(h.Root, action.Id, new Dictionary<string, string> { ["BRANCH"] = "main" }, deadline.Token));
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await h.InScope(async s =>
            {
                var deletion = new ActionDeletionService(s.GetRequiredService<StanzaDbContext>(), s.GetRequiredService<AutomationService>(), s.GetRequiredService<AutomationAuthorization>());
                var preview = await deletion.PreviewAsync(h.Root, action.Id);
                await deletion.DeleteAsync(h.Root, action.Id, preview.Revision, preview.Fingerprint, true, false);
                return true;
            });
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<AutomationException>(() => queue);
        await h.InScope(async s => { Assert.False(await s.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().AnyAsync()); return true; });
    }

    [Fact]
    public async Task FailureToPersistDeletionAuditRollsBackDefinitionDependenciesMemoryAndGrants()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var run = await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync();
        await s.Schedule(action.Id); await s.Hook(action.Id); await s.Grant("action.execute", "action", action.Id);
        var stamp = (await s.Db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.User)).SecurityStamp;
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectActionDeletionAudit BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'automation.action.deleted'
            BEGIN SELECT RAISE(ABORT, 'fixture audit failure'); END;
            """);
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true));
        Assert.True(await s.Db.Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
        Assert.True(await s.Db.Set<AutomationSchedule>().AnyAsync(x => x.ActionId == action.Id));
        Assert.True(await s.Db.Set<WebhookDefinition>().AnyAsync(x => x.ActionId == action.Id));
        Assert.True(await s.Db.Set<ActionInputMemory>().AnyAsync(x => x.ActionId == action.Id));
        Assert.True(await s.Db.Grants.AnyAsync(x => x.ResourceId == action.Id));
        Assert.Equal(stamp, (await s.Db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.User)).SecurityStamp);
        Assert.Equal("succeeded", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Fact]
    public async Task ConfirmedDeletionRemovesOnlyActionConfigurationAndRetainsHistoricalEvidence()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("deploy", s.Deployment)], [new("MODE", Default: "safe")], s.Deployment));
        var other = await s.Action();
        await s.Grant("action.execute", "action", action.Id); await s.Grant("action.execute", "action", other.Id);
        var group = await s.Access.CreateGroupAsync(s.Root, "QA");
        await s.Access.AddMemberAsync(s.Root, group.Id, s.User);
        await s.Access.AddGrantAsync(s.Root, null, group.Id, "action.read", "action", action.Id);
        var run = await s.Service.QueueActionAsync(s.User, action.Id, new Dictionary<string, string> { ["MODE"] = "full" });
        Assert.True(await s.Executor().ExecuteNextAsync());
        var before = await s.Service.ReadRunAsync(s.User, run.Id);
        var operationId = Assert.Single(before.Steps).OperationId!.Value;
        s.Db.Add(new OperationEntity { Id = operationId, ActorId = s.Root, DeploymentId = s.Deployment, Kind = "deploy", State = "Succeeded", ProtectedRequest = "immutable-request", ProtectedResponse = "immutable-response" });
        s.Db.Add(new OperationOutputEntity { OperationId = operationId, Sequence = 1, Stream = "stdout", ProtectedText = "immutable-output" });
        await s.Db.SaveChangesAsync();
        var schedule = await s.Schedule(action.Id); var hook = await s.Hook(action.Id);
        var otherSchedule = await s.Schedule(other.Id); var otherHook = await s.Hook(other.Id);
        s.Db.Add(new WebhookDelivery { Id = Guid.NewGuid(), WebhookId = hook.Definition.Id, DeliveryId = "historical", Event = "push", RunId = run.Id, State = "accepted", ReceivedAt = s.Service.Now });
        await s.Db.SaveChangesAsync(); s.Db.ChangeTracker.Clear();
        var stamp = (await s.Db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.User)).SecurityStamp;
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        Assert.Null(preview.BlockedReason); Assert.Equal(schedule.Id, Assert.Single(preview.Schedules).Id); Assert.Equal(hook.Definition.Id, Assert.Single(preview.Webhooks).Id);
        await Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true);

        Assert.False(await s.Db.Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
        Assert.False(await s.Db.Set<ActionInputMemory>().AnyAsync(x => x.ActionId == action.Id));
        Assert.False(await s.Db.Set<AutomationSchedule>().AnyAsync(x => x.ActionId == action.Id));
        Assert.False(await s.Db.Set<WebhookDefinition>().AnyAsync(x => x.ActionId == action.Id));
        Assert.False(await s.Db.Grants.AnyAsync(x => x.ResourceType == "action" && x.ResourceId == action.Id));
        Assert.NotEqual(stamp, (await s.Db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.User)).SecurityStamp);
        Assert.Equal(otherSchedule.Id, (await s.Db.Set<AutomationSchedule>().SingleAsync()).Id);
        Assert.Equal(otherHook.Definition.Id, (await s.Db.Set<WebhookDefinition>().SingleAsync()).Id);
        Assert.True(await s.Db.Grants.AnyAsync(x => x.ResourceId == other.Id));
        Assert.Equal("immutable-output", (await s.Db.Set<OperationOutputEntity>().SingleAsync()).ProtectedText);
        Assert.Equal("immutable-request", (await s.Db.Set<OperationEntity>().SingleAsync()).ProtectedRequest);
        Assert.Equal("immutable-response", (await s.Db.Set<OperationEntity>().SingleAsync()).ProtectedResponse);
        Assert.Equal(hook.Definition.Id, (await s.Db.Set<WebhookDelivery>().SingleAsync()).WebhookId);
        var retained = await s.Service.ReadRunAsync(s.Root, run.Id);
        Assert.False(retained.ActionAvailable); Assert.Equal(action.Id, retained.Run.ActionId);
        Assert.Equal(before.Run.ProtectedPlan, retained.Run.ProtectedPlan); Assert.Equal(before.Run.ProtectedParameters, retained.Run.ProtectedParameters);
        Assert.Equal(before.Steps[0].OperationId, Assert.Single(retained.Steps).OperationId);
        Assert.Equal("succeeded", retained.Run.State);
        Assert.Contains((await s.Service.ListAsync(s.Root)).Runs, x => x.Id == run.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ReadRunAsync(s.User, run.Id));
        Assert.True(await s.Db.Set<AuditEvent>().AnyAsync(x => x.Action == "automation.action.deleted" && x.Resource == action.Id.ToString()));
        Assert.Equal(404, (await s.Receiver().ReceiveAsync(hook.Definition.Id, "", "new-delivery", "push", Encoding.UTF8.GetBytes("{}"))).Status);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.Root, action.Id));
        // The name is reusable; a new action has a fresh identity and inherits no grants.
        var replacement = await s.Service.SaveActionAsync(s.Root, null, 0, action.Name, true, "queue", new([new("deploy", s.Deployment)], DeploymentId: s.Deployment));
        Assert.NotEqual(action.Id, replacement.Id);
        Assert.False(await s.Access.IsAllowedAsync(s.User, "action.execute", "action", replacement.Id));
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)]
    public async Task MissingActionOrDependencyConfirmationHasNoSideEffects(bool confirmed, bool linked)
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await s.Schedule(action.Id); await s.Hook(action.Id); await s.Grant("action.execute", "action", action.Id);
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        await Assert.ThrowsAsync<AutomationException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, confirmed, linked));
        Assert.True(await s.Db.Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
        Assert.Single(await s.Db.Set<AutomationSchedule>().ToListAsync()); Assert.Single(await s.Db.Set<WebhookDefinition>().ToListAsync());
        Assert.True(await s.Db.Grants.AnyAsync(x => x.ResourceId == action.Id));
        Assert.False(await s.Db.Set<AuditEvent>().AnyAsync(x => x.Action == "automation.action.deleted"));
    }

    [Theory]
    [InlineData("queued")][InlineData("running")][InlineData("uncertain")]
    public async Task QueuedRunningAndUncertainRunsBlockDeletionEvenWhenTheConfirmationPageWasPreviouslyClear(string state)
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        await s.Db.Set<AutomationRun>().Where(x => x.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, state));
        Assert.NotNull((await Deletion(s).PreviewAsync(s.Root, action.Id)).BlockedReason);
        await Assert.ThrowsAsync<AutomationException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true));
        Assert.Equal(state, (await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync()).State);
        Assert.True(await s.Db.Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
    }

    [Fact]
    public async Task ActiveRunLockIsNeverRemovedByDeletionEvenWithoutAnActiveRunRow()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action(); var active = Guid.NewGuid();
        await s.Db.Set<ActionDefinition>().Where(x => x.Id == action.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, active));
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id); Assert.NotNull(preview.BlockedReason);
        await Assert.ThrowsAsync<AutomationException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true));
        Assert.Equal(active, (await s.Db.Set<ActionDefinition>().AsNoTracking().SingleAsync()).ActiveRunId);
    }

    [Theory]
    [InlineData("Queued", false)][InlineData("Running", true)][InlineData("CancellationRequested", false)][InlineData("Unknown", true)]
    public async Task PendingOrUncertainOperationsBlockDeletionEvenWhenTheRunIsTerminal(string state, bool throughStep)
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action(); var operation = Guid.NewGuid(); var run = Guid.NewGuid();
        s.Db.Add(new OperationEntity { Id = operation, State = state });
        s.Db.Add(new AutomationRun { Id = run, ActionId = action.Id, OwnerId = s.Root, RequestedBy = s.Root, State = "failed", OccurrenceKey = "test", OperationId = throughStep ? null : operation });
        if (throughStep) s.Db.Add(new AutomationStepRun { RunId = run, Index = 0, OperationId = operation, State = "failed" });
        await s.Db.SaveChangesAsync();
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id); Assert.NotNull(preview.BlockedReason);
        await Assert.ThrowsAsync<AutomationException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true));
        Assert.Equal(state, (await s.Db.Set<OperationEntity>().AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData("action")][InlineData("new-schedule")][InlineData("new-webhook")][InlineData("changed-schedule")][InlineData("rotated-webhook")]
    public async Task ChangedActionOrLinkedTriggerRequiresAFreshConfirmation(string change)
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var schedule = await s.Schedule(action.Id); var hook = await s.Hook(action.Id);
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        if (change == "action") await s.Service.SaveActionAsync(s.Root, action.Id, action.Revision, "Renamed", false, "queue", new([new("deploy", s.Deployment)]));
        if (change == "new-schedule") await s.Schedule(action.Id);
        if (change == "new-webhook") await s.Hook(action.Id);
        if (change == "changed-schedule") await s.Service.SetScheduleEnabledAsync(s.Root, schedule.Id, false);
        if (change == "rotated-webhook") await s.Service.RotateWebhookAsync(s.Root, hook.Definition.Id, false);
        var error = await Assert.ThrowsAsync<AutomationException>(() => Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, true));
        Assert.Contains("changed", error.Message); Assert.True(await s.Db.Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
        Assert.True(await s.Db.Set<AutomationSchedule>().AnyAsync()); Assert.True(await s.Db.Set<WebhookDefinition>().AnyAsync());
    }

    [Fact]
    public async Task ActionExecutionDoesNotAuthorizeDeletionAndActionManagementDoesNotAuthorizeDeletingOtherDeploymentsWebhooks()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await s.Grant("action.execute", "action", action.Id);
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => Deletion(s).PreviewAsync(s.User, action.Id));
        await Assert.ThrowsAsync<AutomationAccessException>(() => Deletion(s).DeleteAsync(s.User, action.Id, preview.Revision, preview.Fingerprint, true, true));
        await s.Grant("action.manage", "action", action.Id); await s.Hook(action.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => Deletion(s).PreviewAsync(s.User, action.Id));
        await s.Grant("automation.manage", "deployment", s.Deployment);
        preview = await Deletion(s).PreviewAsync(s.User, action.Id);
        await Deletion(s).DeleteAsync(s.User, action.Id, preview.Revision, preview.Fingerprint, true, true);
        Assert.False(await s.Db.Set<ActionDefinition>().AnyAsync());
    }

    [Fact]
    public async Task DeletedActionHistoryDoesNotRestoreRevokedAccessOrBypassCurrentPermissions()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await s.Grant("action.execute", "action", action.Id);
        var run = await s.Service.QueueActionAsync(s.User, action.Id); await s.Executor().ExecuteNextAsync();
        var unrelated = await s.Accounts.CreateAsync(s.Root, "other", "a-secure-password-123", AccountRole.User);
        var preview = await Deletion(s).PreviewAsync(s.Root, action.Id);
        await Deletion(s).DeleteAsync(s.Root, action.Id, preview.Revision, preview.Fingerprint, true, false);
        Assert.False((await s.Service.ReadRunAsync(s.Root, run.Id)).ActionAvailable);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ReadRunAsync(s.User, run.Id));
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ReadRunAsync(unrelated.Id, run.Id));
        await s.Grant("action.read", "action", null);
        Assert.False((await s.Service.ReadRunAsync(s.User, run.Id)).ActionAvailable);
        await s.Db.Accounts.Where(x => x.Id == s.User).ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, false));
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ReadRunAsync(s.User, run.Id));
    }
}
