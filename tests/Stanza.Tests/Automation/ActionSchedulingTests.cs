using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class ActionSchedulingTests
{
    [Fact]
    public async Task EachOccurrenceSnapshotsLatestSharedValuesAndManualScheduleRunsDoNotReplaceMemory()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("container-exec", s.Deployment, Execution: new("/usr/bin/reset-db", ["${database}"], Service: "app"))],
            [new("database", Input: "dropdown", Options: ["testing", "demo"], Default: "testing")], s.Deployment));
        await s.Grant("action.execute", "action", action.Id);
        await s.Service.QueueActionAsync(s.User, action.Id, new Dictionary<string, string> { ["database"] = "demo" });
        var schedule = await Save(s, action.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var first = await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Source == "schedule");
        Assert.Equal("demo", s.Service.Unprotect<Dictionary<string, string>>(first.ProtectedParameters)["database"]);
        await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["database"] = "testing" });
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var second = await s.Db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.Equal("testing", s.Service.Unprotect<Dictionary<string, string>>(second.ProtectedParameters)["database"]);
        Assert.Equal("demo", s.Service.Unprotect<Dictionary<string, string>>(first.ProtectedParameters)["database"]);
        var before = (await s.Db.Set<ActionInputMemory>().AsNoTracking().SingleAsync()).ProtectedValues;
        var manual = await s.Service.RunScheduleAsync(s.Root, schedule.Id);
        Assert.Equal("testing", s.Service.Unprotect<Dictionary<string, string>>(manual.ProtectedParameters)["database"]);
        Assert.Equal(before, (await s.Db.Set<ActionInputMemory>().AsNoTracking().SingleAsync()).ProtectedValues);
    }

    [Fact]
    public async Task HostMaintenanceCanBeScheduledWithoutDeploymentAndUsesSharedLastUsedArguments()
    {
        await using var s = await AutomationTestStore.Create(); var agent = Guid.NewGuid();
        var action = await s.Action(new([new("host-exec", AgentId: agent, Execution: new("/usr/bin/docker", ["system", "prune", "--force", "--filter", "${filter}"]))],
            [new("filter", Default: "until=24h")]));
        var schedule = await Save(s, action.Id);
        Assert.Null(schedule.DeploymentId);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        await s.Executor().ExecuteNextAsync();
        var initial = Assert.Single(s.Operations.Submissions);
        Assert.Equal(agent, initial.AgentId); Assert.Null(initial.DeploymentId); Assert.Equal("host-exec", initial.Kind);
        Assert.Equal(new[] { "system", "prune", "--force", "--filter", "until=24h" }, initial.Execution!.Arguments);
        Assert.DoesNotContain("--volumes", initial.Execution.Arguments);
        await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["filter"] = "until=72h" });
        Assert.Equal("until=72h", (await s.Service.InvocationAsync(s.Root, action.Id, default)).Values["filter"]);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var latest = await s.Db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.Equal("until=72h", s.Service.Unprotect<Dictionary<string, string>>(latest.ProtectedParameters)["filter"]);
        Assert.Equal(Guid.Empty, (await s.Db.Set<ActionInputMemory>().AsNoTracking().SingleAsync()).DeploymentId);
        Assert.Single(await s.Service.ActionSchedulesAsync(s.Root, action.Id));
    }

    [Fact]
    public async Task MissingInputsCanBeConfiguredPausedButCannotBeEnabledAndMissingLaterInputsAdvanceAsDenied()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("restart", s.Deployment)], [new("required")], s.Deployment));
        await Assert.ThrowsAsync<AutomationException>(() => Save(s, action.Id));
        var schedule = await Save(s, action.Id, enabled: false);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SetScheduleEnabledAsync(s.Root, schedule.Id, true));
        await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["required"] = "chosen" });
        await s.Service.SetScheduleEnabledAsync(s.Root, schedule.Id, true);
        await s.Db.Set<ActionInputMemory>().ExecuteDeleteAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync(); await s.Scheduler().TickAsync();
        var denied = Assert.Single(await s.Db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").ToListAsync());
        Assert.Equal("denied", denied.State); Assert.Equal("target-or-inputs-unavailable", denied.Code);
        Assert.True((await s.Db.Set<AutomationSchedule>().AsNoTracking().SingleAsync()).NextRunAt > s.Service.Now);
    }

    [Fact]
    public async Task IncompatibleRememberedInputIsDeniedInsteadOfSilentlyChangingToDefault()
    {
        await using var s = await AutomationTestStore.Create();
        var plan = new ActionPlan([new("restart", s.Deployment)], [new("mode", Default: "one", Input: "dropdown", Options: ["one", "two"])], s.Deployment);
        var action = await s.Action(plan);
        await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["mode"] = "two" }); await Save(s, action.Id);
        await s.Service.SaveActionAsync(s.Root, action.Id, action.Revision, "Edited", true, "queue", plan with { Parameters = [new("mode", Default: "one", Input: "dropdown", Options: ["one", "three"])] });
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        Assert.Equal("denied", (await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Source == "schedule")).State);
    }

    [Fact]
    public async Task ExecuteOnlyGrantCannotManageSchedulesAndRevokedManagementStopsQueuedCron()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await s.Grant("action.execute", "action", action.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => Save(s, action.Id, actor: s.User));
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.ActionSchedulesAsync(s.User, action.Id));
        var management = await s.Grant("action.manage", "action", action.Id);
        var schedule = await Save(s, action.Id, actor: s.User);
        Assert.False(await s.Access.IsAllowedAsync(s.User, "deployment.deploy", "deployment", s.Deployment));
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var run = await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync(); Assert.Equal("queued", run.State);
        await s.Access.RemoveGrantAsync(s.Root, management.Id); await s.Executor().ExecuteNextAsync();
        Assert.Empty(s.Operations.Submissions); Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.RunScheduleAsync(s.User, schedule.Id));
    }

    [Fact]
    public async Task EmbeddedScheduleCannotOverwriteAnotherActionScheduleAndHostActionsMayBeConfiguredWhilePaused()
    {
        await using var s = await AutomationTestStore.Create(); var first = await s.Action(); var other = await s.Action();
        var schedule = await Save(s, first.Id);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionScheduleAsync(s.Root, other.Id, schedule.Id, schedule.Revision,
            "Wrong action", "* * * * *", "UTC", true, "skip", "skip"));
        await s.Service.SaveActionAsync(s.Root, other.Id, other.Revision, "Paused", false, "queue", new([new("restart", s.Deployment)]));
        var paused = await Save(s, other.Id, enabled: false); Assert.False(paused.Enabled);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SetScheduleEnabledAsync(s.Root, paused.Id, true));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveScheduleAsync(s.Root, new() { Name = "No target" }, 0));
    }

    private static Task<AutomationSchedule> Save(AutomationTestStore s, Guid actionId, bool enabled = true, Guid? actor = null) =>
        s.Service.SaveActionScheduleAsync(actor ?? s.Root, actionId, Guid.Empty, 0, "Periodic action", "* * * * *", "UTC", enabled, "queue", "skip");

    [Fact]
    public async Task FullQueueRecordsDeniedOccurrenceOnceInsteadOfRetryingOnEveryTick()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await Save(s, action.Id); await s.Service.SaveLimitsAsync(s.Root, new(MaximumQueuedRuns: 1));
        await s.Service.QueueActionAsync(s.Root, action.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync(); await s.Scheduler().TickAsync();
        var denied = Assert.Single(await s.Db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").ToListAsync());
        Assert.Equal("denied", denied.State); Assert.Equal("queue-or-inputs-unavailable", denied.Code);
        Assert.Single(await s.Db.Set<AutomationRun>().Where(x => x.State == "queued").ToListAsync());
    }
}
