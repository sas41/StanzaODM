using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class ExecutionConcurrencyTests
{
    [Fact]
    public async Task ALongBlockedQueueOnOneAgentCannotHideRunnableWorkForAnotherAgent()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            db.Add(new OperationEntity { Id = Guid.NewGuid(), AgentId = h.Agent, DeploymentId = h.Deployment, Kind = "deploy", State = "Running",
                LeaseUntil = h.Clock.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds() });
            for (var i = 0; i < 200; i++) db.Add(new OperationEntity { Id = Guid.NewGuid(), AgentId = h.Agent, DeploymentId = h.Deployment, Kind = "deploy", State = "Queued", ProtectedRequest = "must-not-be-read" });
            await db.SaveChangesAsync(); return true;
        });
        var agent = await h.AddAgentAsync("free"); var deployment = await h.AddDeploymentAsync("free", agent);
        var ready = await h.InScope(s => s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, deployment, "deploy")));
        await h.StartWorkerAsync(); await h.WaitStateAsync(ready.Id, "Succeeded");
        Assert.Equal(agent, Assert.Single(h.Dispatcher.Requests).Agent);
        await h.InScope(async s => { Assert.Equal(200, await s.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().CountAsync(x => x.AgentId == h.Agent && x.State == "Queued")); return true; });
    }

    [Fact]
    public async Task AutomationWorkerStartsIndependentWorkflowsWithoutAwaitingTheFirstWorkflowsCompletion()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var agent = await h.AddAgentAsync("second"); var deployment = await h.AddDeploymentAsync("second", agent);
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            foreach (var target in new[] { h.Deployment, deployment })
            {
                var action = await automation.SaveActionAsync(h.Root, null, 0, target.ToString(), true, "queue", new([new("deploy", target)], DeploymentId: target));
                await automation.QueueActionAsync(h.Root, action.Id);
            }
            return true;
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, ct) => { await release.Task.WaitAsync(ct); return new(request.Id, true, "ok", "[]"); };
        using var worker = new AutomationWorker(h.Services.GetRequiredService<IServiceScopeFactory>(), h.Clock, NullLogger<AutomationWorker>.Instance);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await OperationTestHarness.UntilAsync(() => h.InScope(async s => await s.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().CountAsync() == 2), "Both workflows should enqueue work before either finishes.");
            await h.StartWorkerAsync(); await h.Dispatcher.WaitForCountAsync(2);
            Assert.Equal(2, h.Dispatcher.Requests.Select(x => x.Agent).Distinct().Count());
        }
        finally
        {
            release.TrySetResult(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task StandaloneMutationsWaitForWorkflowReservationsButReadOnlyInspectionDoesNot()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            db.Add(new DeploymentExecutionReservation { DeploymentId = h.Deployment, RunId = Guid.NewGuid() });
            await db.SaveChangesAsync(); return true;
        });
        var deploy = await h.EnqueueAsync("deploy"); var status = await h.EnqueueAsync("status");
        await h.StartWorkerAsync(); await h.WaitStateAsync(status.Id, "Succeeded");
        Assert.Equal("Queued", (await h.RowAsync(deploy.Id)).State);
        Assert.Equal("status", Assert.Single(h.Dispatcher.Requests).Request.Operation);
        await h.InScope(async s => { await s.GetRequiredService<StanzaDbContext>().Set<DeploymentExecutionReservation>().ExecuteDeleteAsync(); return true; });
        await h.NextTickAsync(); await h.WaitStateAsync(deploy.Id, "Succeeded");
    }

    [Fact]
    public async Task FiveIndependentAgentsRunConcurrentlyWithoutTheOldGlobalLimitOfFour()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, token) => { await release.Task.WaitAsync(token); return new(request.Id, true, "ok", "[]"); };
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var agent = await h.AddAgentAsync("agent-" + i); var deployment = await h.AddDeploymentAsync("deployment-" + i, agent);
                await h.InScope(s => s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, deployment, "deploy")));
            }
            await h.StartWorkerAsync(); await h.Dispatcher.WaitForCountAsync(5);
            Assert.Equal(5, h.Dispatcher.Requests.Select(x => x.Agent).Distinct().Count());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(1, 1)][InlineData(2, 2)]
    public async Task AgentPolicyControlsParallelDifferentDeploymentsWhileEachDeploymentRemainsSerial(int limit, int expected)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s => { await s.GetRequiredService<AgentPolicyService>().SaveAsync(h.Root, new(h.Agent, new(MaximumParallelOperations: limit), 0)); return true; });
        var other = await h.AddDeploymentAsync("other", h.Agent);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, token) => { await release.Task.WaitAsync(token); return new(request.Id, true, "ok", "[]"); };
        var a = await h.EnqueueAsync("deploy"); h.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = await h.EnqueueAsync("deploy"); h.Clock.Advance(TimeSpan.FromSeconds(1));
        var b = await h.InScope(s => s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, other, "deploy")));
        try
        {
            await h.StartWorkerAsync(); await h.Dispatcher.WaitForCountAsync(expected);
            await h.WaitStateAsync(again.Id, "Queued");
            if (limit == 1) Assert.Equal("Queued", (await h.RowAsync(b.Id)).State);
            Assert.Equal(expected, h.Dispatcher.Requests.Count);
            Assert.Equal(expected, h.Dispatcher.Requests.Select(x => x.Request.DeploymentId).Distinct().Count());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task WorkflowReservationSpansEveryStepAndBlocksOtherActionsAndStandaloneOperations()
    {
        await using var s = await Stanza.Tests.Automation.AutomationTestStore.Create();
        var first = await s.Action(new([new("deploy", s.Deployment), new("restart", s.Deployment)], DeploymentId: s.Deployment));
        var otherAction = await s.Action(new([new("deploy", s.Deployment)], DeploymentId: s.Deployment));
        var independent = await s.Action(new([new("deploy", s.OtherDeployment)], DeploymentId: s.OtherDeployment));
        var run = await s.Service.QueueActionAsync(s.Root, first.Id);
        var blocked = await s.Service.QueueActionAsync(s.Root, otherAction.Id);
        var parallel = await s.Service.QueueActionAsync(s.Root, independent.Id);
        var started = new SemaphoreSlim(0); var proceed = new SemaphoreSlim(0);
        s.Operations.OnWait = async token => { started.Release(); await proceed.WaitAsync(token); };
        var executor = s.Executor(); Assert.True(await executor.ClaimAsync(run));
        var execute = executor.ExecuteClaimedAsync(run);
        try
        {
            await started.WaitAsync(TimeSpan.FromSeconds(5));
            await using var db = s.NewContext(); var auth = new AutomationAuthorization(new(db, s.Clock));
            var other = new AutomationExecutor(db, new(db, auth, s.Protection, s.Clock), auth, new Stanza.Tests.Automation.FakeOperations(), new UnavailableImageDigestProbe(), s.Clock);
            Assert.False(await other.ClaimAsync(blocked)); Assert.True(await other.ClaimAsync(parallel));
            Assert.Equal(run.Id, (await db.Set<DeploymentExecutionReservation>().AsNoTracking().SingleAsync(x => x.DeploymentId == s.Deployment)).RunId);
            proceed.Release(); Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(await other.ClaimAsync(blocked));
            proceed.Release(); await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await db.Set<DeploymentExecutionReservation>().AnyAsync(x => x.RunId == run.Id));
            Assert.True(await other.ClaimAsync(blocked));
        }
        finally { proceed.Release(3); await execute; }
    }

    [Fact]
    public async Task UncertainWorkflowRetainsItsDeploymentReservation()
    {
        await using var s = await Stanza.Tests.Automation.AutomationTestStore.Create(); var action = await s.Action();
        var run = await s.Service.QueueActionAsync(s.Root, action.Id); s.Operations.State = "Unknown";
        await s.Executor().ExecuteNextAsync();
        Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        Assert.Equal(run.Id, (await s.Db.Set<DeploymentExecutionReservation>().SingleAsync()).RunId);
    }
}
