using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;

namespace Stanza.Tests.Automation;

public sealed class SchedulerTests
{
    [Fact]
    public async Task DueOccurrenceIsAtomicallyClaimedOnceAcrossFreshContexts()
    {
        await using var s = await AutomationTestStore.Create(); var schedule = await s.Schedule();
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await s.Scheduler().ClaimScheduleAsync(schedule));
        await using var secondDb = s.NewContext();
        var access = new Stanza.ControlPlane.Security.AccessService(secondDb, s.Clock);
        var auth = new AutomationAuthorization(access);
        var service = new AutomationService(secondDb, auth, s.Protection, s.Clock);
        Assert.False(await new AutomationScheduler(secondDb, service, auth, s.Clock).ClaimScheduleAsync(schedule));
        var run = Assert.Single(await secondDb.Set<AutomationRun>().AsNoTracking().ToListAsync());
        Assert.Equal("queued", run.State); Assert.Contains(schedule.NextRunAt.ToString(), run.OccurrenceKey);
        Assert.Equal(schedule.NextRunAt + 60, (await secondDb.Set<AutomationSchedule>().AsNoTracking().SingleAsync()).NextRunAt);
    }

    [Theory]
    [InlineData("skip", "skipped")][InlineData("run-once", "queued")]
    public async Task DowntimeDoesNotProduceUnboundedCatchup(string missedPolicy, string state)
    {
        await using var s = await AutomationTestStore.Create(); await s.Schedule(missed: missedPolicy);
        s.Clock.Advance(TimeSpan.FromDays(10)); await s.Scheduler().TickAsync(); await s.Scheduler().TickAsync();
        var run = Assert.Single(await s.Db.Set<AutomationRun>().AsNoTracking().ToListAsync()); Assert.Equal(state, run.State);
        var schedule = await s.Db.Set<AutomationSchedule>().AsNoTracking().SingleAsync(); Assert.True(schedule.NextRunAt > s.Service.Now);
    }

    [Theory]
    [InlineData("skip", "skipped")][InlineData("queue", "queued")]
    public async Task OverlapPolicyRecordsEachOccurrenceWithClearOutcome(string overlap, string state)
    {
        await using var s = await AutomationTestStore.Create(); var schedule = await s.Schedule(overlap: overlap);
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(1)); await s.Scheduler().TickAsync();
        var runs = await s.Db.Set<AutomationRun>().AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, runs.Count); Assert.Equal("queued", runs[0].State); Assert.Equal(state, runs[1].State);
    }

    [Fact]
    public async Task PausedAndRevokedSchedulesNeverDispatchAndManualRunsUseCallerIdentity()
    {
        await using var s = await AutomationTestStore.Create(); var schedule = await s.Schedule();
        await s.Service.SetScheduleEnabledAsync(s.Root, schedule.Id, false); s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.Scheduler().TickAsync(); Assert.Empty(await s.Db.Set<AutomationRun>().ToListAsync());
        await s.Grant("automation.manage", "deployment", s.Deployment); await s.Grant("deployment.deploy", "deployment", s.Deployment);
        var manual = await s.Service.RunScheduleAsync(s.User, schedule.Id); Assert.Equal(s.User, manual.OwnerId);
        await s.Service.SaveScheduleAsync(s.User, new() { Id = schedule.Id, Name = "User-owned", DeploymentId = s.Deployment,
            Expression = "* * * * *", Enabled = true }, 2);
        await s.Accounts.SetEnabledAsync(s.Root, s.User, false); s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Scheduler().TickAsync();
        Assert.Contains(await s.Db.Set<AutomationRun>().AsNoTracking().ToListAsync(), x => x.Source == "schedule" && x.State == "denied");
        while (await s.Executor().ExecuteNextAsync()) { }
        Assert.Empty(s.Operations.Submissions);
    }

    [Fact]
    public async Task InterruptedLeaseIsUncertainNotReplayedAndRetainsLock()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        Assert.True(await s.Executor().ClaimAsync(run)); s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Scheduler().RecoverExpiredAsync(); await s.Scheduler().RecoverExpiredAsync();
        Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        Assert.Equal(run.Id, (await s.Db.Set<ActionDefinition>().AsNoTracking().SingleAsync()).ActiveRunId);
        Assert.False(await s.Executor().ExecuteNextAsync()); Assert.Empty(s.Operations.Submissions);
    }

    [Fact]
    public async Task ScheduleRejectsInvalidTimezoneImpossibleCronAndUnboundRequiredParameters()
    {
        await using var s = await AutomationTestStore.Create();
        Assert.Throws<AutomationException>(() => s.Service.Preview("* * * * *", "not/a-zone"));
        Assert.Throws<AutomationException>(() => s.Service.Preview("0 0 31 2 *", "UTC"));
        var action = await s.Action(new([new("deploy", s.Deployment)], [new("required")]));
        await Assert.ThrowsAsync<AutomationException>(() => s.Schedule(action.Id));
    }

    [Fact]
    public async Task QueueLimitsAndRetentionAreUiConfigurableAndUncertainRunsSurvivePruning()
    {
        await using var s = await AutomationTestStore.Create();
        await s.Service.SaveLimitsAsync(s.Root, new(MaximumQueuedRuns: 1, HistoryRetentionDays: 1));
        Assert.Equal(1, (await s.Service.LimitsAsync()).MaximumQueuedRuns);
        var action = await s.Action(); var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.Root, action.Id));
        s.Operations.State = "Unknown"; await s.Executor().ExecuteNextAsync(); s.Clock.Advance(TimeSpan.FromDays(2));
        await s.Scheduler().TickAsync(); Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.SaveLimitsAsync(s.User, new()));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveLimitsAsync(s.Root, new(MaximumWebhookBytes: 0)));
    }
}
