using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;
using Stanza.Tests.Automation;

namespace Stanza.Tests.Operations;

public sealed class OperationRecoveryTests
{
    [Theory]
    [InlineData("interrupted_before_apply", false, false)]
    [InlineData("succeeded", true, false)]
    [InlineData("uncertain", true, true)]
    [InlineData("runtime_failed", true, true)]
    public async Task SingleInspectionRefreshesContainersAndAtomicallyResolvesOperationRunAndBothLocks(string code, bool applied, bool acknowledge)
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        var dispatcher = (RecoverableOperationTestDispatcher)h.Dispatcher;
        dispatcher.Jobs[operation] = Evidence(operation, code, applied);
        dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "succeeded", "[]", ExecutionStopped: true));
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            db.Add(new ImageUpdatePolicy { Id = Guid.NewGuid(), DeploymentId = h.Deployment, OwnerId = h.Root, ActiveRunId = run });
            await db.SaveChangesAsync(); return true;
        });
        var message = await h.InScope(s => s.GetRequiredService<OperationRecoveryService>().InspectOperationAsync(h.Root, operation, acknowledge));
        Assert.Contains("together", message); Assert.Equal(code == "succeeded" ? "Succeeded" : "Failed", (await h.RowAsync(operation)).State);
        var status = Assert.Single(dispatcher.Requests).Request;
        Assert.Equal("status", status.Operation); Assert.Empty(status.Files); Assert.Empty(status.EnvironmentFiles);
        Assert.DoesNotContain(dispatcher.Requests, x => x.Request.Id == operation);
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>(); var saved = await db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Id == run);
            Assert.Equal("reconciled", saved.State); Assert.Equal(h.Root, saved.ReconciledBy);
            Assert.Null((await db.Set<ActionDefinition>().AsNoTracking().SingleAsync(x => x.Id == action)).ActiveRunId);
            Assert.Null((await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync()).ActiveRunId);
            Assert.Equal("absent", (await db.Set<DeploymentRuntimeEntity>().SingleAsync()).ObservedState);
            Assert.NotEqual("uncertain", (await db.Set<AutomationStepRun>().SingleAsync()).State);
            return true;
        });
        await h.InScope(s => s.GetRequiredService<OperationRecoveryService>().InspectRunAsync(h.Root, run, acknowledge));
        Assert.Single(dispatcher.Requests); // Reinspection neither retries the command nor repeats the status mutation.
    }

    [Theory]
    [InlineData("active")]
    [InlineData("missing")]
    [InlineData("orphaned")]
    [InlineData("unacknowledged-apply")]
    [InlineData("failed-status")]
    public async Task UnresolvedEvidenceAndTimeoutsNeverReleaseEitherLock(string condition)
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        var dispatcher = (RecoverableOperationTestDispatcher)h.Dispatcher;
        dispatcher.Jobs[operation] = condition switch
        {
            "active" => Evidence(operation, "uncertain", false) with { Active = true, Stopped = false },
            "missing" => Evidence(operation, "uncertain", false) with { Found = false, Stopped = false },
            "orphaned" => Evidence(operation, "uncertain", true) with { Stopped = false, Response = null },
            "unacknowledged-apply" => Evidence(operation, "uncertain", true),
            _ => Evidence(operation, "interrupted_before_apply", false)
        };
        dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, false, "status_unavailable"));
        h.Clock.Advance(TimeSpan.FromDays(30));
        await Assert.ThrowsAsync<OperationException>(() => h.InScope(s => s.GetRequiredService<OperationRecoveryService>().InspectRunAsync(h.Root, run, false)));
        await AssertLockedAsync(h, operation, run, action);
    }

    [Fact]
    public async Task ReconciliationRollsBackOperationChangesIfTheRunChangesDuringInspection()
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        var dispatcher = (RecoverableOperationTestDispatcher)h.Dispatcher;
        dispatcher.Jobs[operation] = Evidence(operation, "interrupted_before_apply", false);
        dispatcher.Handler = async (_, request, _) =>
        {
            await h.InScope(async s => { await s.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().Where(x => x.Id == run)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "running")); return true; });
            return new(request.Id, true, "succeeded", "[]");
        };
        await Assert.ThrowsAsync<OperationException>(() => h.InScope(s => s.GetRequiredService<OperationRecoveryService>().InspectRunAsync(h.Root, run, false)));
        Assert.Equal("Unknown", (await h.RowAsync(operation)).State);
        await h.InScope(async s => { Assert.Equal(run, (await s.GetRequiredService<StanzaDbContext>().Set<ActionDefinition>().SingleAsync(x => x.Id == action)).ActiveRunId); return true; });
    }

    [Fact]
    public async Task UnauthorizedRecoveryDoesNotContactTheAgentOrChangeLocks()
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        await Assert.ThrowsAsync<OperationException>(() => h.InScope(s => s.GetRequiredService<OperationRecoveryService>().InspectOperationAsync(h.User, operation, true)));
        Assert.Empty(((RecoverableOperationTestDispatcher)h.Dispatcher).Attached); Assert.Empty(h.Dispatcher.Requests);
        await AssertLockedAsync(h, operation, run, action);
    }

    [Fact]
    public async Task ControlPlaneRestartAttachesToRunningJobWithoutDispatchAndAutomaticallyRecoversKnownResult()
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        var dispatcher = (RecoverableOperationTestDispatcher)h.Dispatcher;
        dispatcher.Jobs[operation] = Evidence(operation, "succeeded", true) with { Active = true, Stopped = false, Response = null };
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<AgentWorkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.OnAttach = async (id, ct) => { attached.TrySetResult(); return await complete.Task.WaitAsync(ct); };
        dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "succeeded", "[]", ExecutionStopped: true));
        using var worker = new OperationRecoveryWorker(h.Services.GetRequiredService<IServiceScopeFactory>(), dispatcher, NullLogger<OperationRecoveryWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await attached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(dispatcher.Requests); Assert.Equal("Unknown", (await h.RowAsync(operation)).State);
            dispatcher.Jobs[operation] = Evidence(operation, "succeeded", true);
            complete.TrySetResult(dispatcher.Jobs[operation].Response!);
            await h.WaitStateAsync(operation, "Succeeded");
            await OperationTestHarness.UntilAsync(() => h.InScope(async s => (await s.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Id == run)).State == "reconciled"));
            Assert.All(dispatcher.Requests, x => Assert.Equal("status", x.Request.Operation));
            await h.InScope(async s => { Assert.Null((await s.GetRequiredService<StanzaDbContext>().Set<ActionDefinition>().SingleAsync(x => x.Id == action)).ActiveRunId); return true; });
        }
        finally { complete.TrySetResult(new(operation, false, "uncertain")); await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RecoveryWorkerShutdownDetachesWithoutSendingCancellationOrReplayingWork()
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        var dispatcher = (RecoverableOperationTestDispatcher)h.Dispatcher;
        dispatcher.Jobs[operation] = Evidence(operation, "succeeded", true) with { Active = true, Stopped = false, Response = null };
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.OnAttach = async (_, ct) => { attached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException("unreachable"); };
        using var worker = new OperationRecoveryWorker(h.Services.GetRequiredService<IServiceScopeFactory>(), dispatcher, NullLogger<OperationRecoveryWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await attached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(deadline.Token);
        Assert.Empty(dispatcher.Requests); Assert.Empty(dispatcher.Cancelled);
        await AssertLockedAsync(h, operation, run, action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserCanRequestCancellationOfUnknownWorkWithoutUnlockingIt(bool throughRun)
    {
        await using var h = await OperationTestHarness.CreateAsync(recoverable: true);
        var (operation, run, action) = await SeedAsync(h);
        if (throughRun)
            await h.InScope(async s => { await s.GetRequiredService<AutomationService>().CancelRunAsync(h.Root, run); return true; });
        else await h.CancelAsync(operation);
        Assert.Equal("user", (await h.RowAsync(operation)).CancellationReason);
        await AssertLockedAsync(h, operation, run, action);
    }

    private static AgentJobInspection Evidence(Guid id, string code, bool applied) => new(id, true, false, true,
        applied ? "Applying" : "Building", applied, 0, 0, 100, code == "interrupted_before_apply" ? "deadline" : null,
        new(id, code == "succeeded", code, ExecutionStopped: true));
    private static async Task<(Guid Operation, Guid Run, Guid Action)> SeedAsync(OperationTestHarness h)
    {
        var result = await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>(); var automation = s.GetRequiredService<AutomationService>();
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Recovery deploy", true, "reject", new([new("deploy", h.Deployment)], DeploymentId: h.Deployment));
            var run = await automation.QueueActionAsync(h.Root, action.Id);
            var executor = new AutomationExecutor(db, automation, s.GetRequiredService<AutomationAuthorization>(), new FakeOperations(), new UnavailableImageDigestProbe(), h.Clock);
            Assert.True(await executor.ClaimAsync(run));
            var operation = await s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, h.Deployment, "deploy", "automation:" + run.Id.ToString("N")));
            await db.Set<AutomationRun>().Where(x => x.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OperationId, operation.Id).SetProperty(x => x.State, "uncertain"));
            db.Add(new AutomationStepRun { RunId = run.Id, Index = 0, Kind = "deploy", State = "uncertain", OperationId = operation.Id, DeploymentId = h.Deployment, AgentId = h.Agent });
            await db.SaveChangesAsync(); return (operation.Id, run.Id, action.Id);
        });
        await h.SetStateAsync(result.Item1, "Unknown", h.Clock.GetUtcNow().ToUnixTimeSeconds());
        return result;
    }
    private static async Task AssertLockedAsync(OperationTestHarness h, Guid operation, Guid run, Guid action)
    {
        Assert.Equal("Unknown", (await h.RowAsync(operation)).State);
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            Assert.Equal("uncertain", (await db.Set<AutomationRun>().SingleAsync(x => x.Id == run)).State);
            Assert.Equal(run, (await db.Set<ActionDefinition>().SingleAsync(x => x.Id == action)).ActiveRunId); return true;
        });
    }
}
