using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentJobStoreTests
{
    [Theory]
    [InlineData("Building")]
    [InlineData("Applying")]
    [InlineData("Checking")]
    public async Task WorkOwnsItsLifetimeBuffersOutputWithoutTransportAndPersistsFinalResult(string phase)
    {
        using var root = new AgentTestDirectory(); var executor = new Executor(phase);
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path });
        var request = AgentWorkExecutorTests.Request("deploy");
        await using (var jobs = new AgentJobStore(options, executor, TimeProvider.System))
        {
            Assert.Null(await jobs.StartAsync(request));
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await jobs.RecordDisconnectAsync("WebSocketException", 1006);
            var active = await jobs.InspectAsync(request.Id);
            Assert.True(active.Active); Assert.False(active.Stopped); Assert.Equal(phase, active.Phase);
            Assert.Equal(50, active.LastSequence); Assert.Equal(0, active.AcknowledgedSequence);
            Assert.Null(await jobs.StartAsync(request)); Assert.Equal(1, executor.Executions);
            Assert.Equal("request_id_conflict", (await jobs.StartAsync(request with { Operation = "remove" }))!.Code);
            Assert.Equal("agent_busy", (await jobs.StartAsync(request with { Id = Guid.NewGuid() }))!.Code);
            await jobs.AcknowledgeAsync(request.Id, 17);
            var replay = await jobs.ReadOutputAsync(request.Id, 17);
            Assert.Equal(Enumerable.Range(18, 33).Select(x => (long)x), replay.Select(x => x.Sequence));
            await Assert.ThrowsAsync<IOException>(() => jobs.AcknowledgeAsync(request.Id, 51));
            executor.Finish.TrySetResult();
            await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
            Assert.True((await jobs.InspectAsync(request.Id)).Response!.Succeeded);
            await jobs.RecordDisconnectAsync("final_result_delivery_failed", null, [request.Id]);
        }
        var replacement = new Executor("Applying");
        await using var reopened = new AgentJobStore(options, replacement, TimeProvider.System);
        Assert.Null(await reopened.StartAsync(request));
        var saved = await reopened.InspectAsync(request.Id);
        Assert.False(saved.Active); Assert.True(saved.Stopped); Assert.Equal(phase, saved.Phase);
        Assert.Equal(17, saved.AcknowledgedSequence); Assert.Equal(0, replacement.Executions);
        Assert.Equal(50, (await reopened.ReadOutputAsync(request.Id, 0)).Count);
        var diagnostic = await File.ReadAllTextAsync(Path.Combine(root.Path, "jobs-v1", "disconnects.jsonl"));
        Assert.Contains("WebSocketException", diagnostic); Assert.Contains(request.Id.ToString(), diagnostic);
        Assert.Contains("final_result_delivery_failed", diagnostic);
        Assert.DoesNotContain("private-output", diagnostic);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("authorization_revoked")]
    [InlineData("shutdown")]
    public async Task ExplicitCancellationPersistsItsCauseWithoutPretendingApplyRolledBack(string reason)
    {
        using var root = new AgentTestDirectory(); var executor = new Executor("Applying");
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), executor, TimeProvider.System);
        var request = AgentWorkExecutorTests.Request("deploy"); await jobs.StartAsync(request);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await jobs.CancelAsync(request.Id, reason);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var result = await jobs.InspectAsync(request.Id);
        Assert.Equal(reason, result.CancellationReason); Assert.True(result.ApplyStarted);
        Assert.False(result.Stopped); Assert.Equal("uncertain", result.Response!.Code);
        Assert.Equal(1, executor.Executions);
    }

    [Fact]
    public async Task UnfinishedJournalAfterAgentRestartIsNotEvidenceThatOrphanedWorkStopped()
    {
        using var root = new AgentTestDirectory(); var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path });
        var id = Guid.NewGuid(); var directory = PrivateStorage.EnsureDirectory(Path.Combine(root.Path, "jobs-v1"));
        var inspection = new AgentJobInspection(id, true, true, false, "Applying", true, 0, 0, 123);
        await File.WriteAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(new { Version = 1, Hash = "old", Job = inspection }));
        await using var jobs = new AgentJobStore(options, new Executor("Building"), TimeProvider.System);
        var result = await jobs.InspectAsync(id);
        Assert.True(result.Found); Assert.False(result.Active); Assert.False(result.Stopped); Assert.Null(result.Response);
    }

    [Fact]
    public async Task UpgradeRetainsOriginalExecutorIdempotencyLedgerAndDoesNotRunAcceptedCommandsAgain()
    {
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner();
        var request = AgentWorkExecutorTests.Request();
        Assert.True((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        var freshRunner = new AgentWorkExecutorTests.FakeRunner();
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            AgentWorkExecutorTests.Executor(root.Path, freshRunner), TimeProvider.System);
        // Inspecting a pre-upgrade terminal receipt is sufficient; it must not require sending
        // the executable snapshot again merely to recover a lost final response.
        var oldReceipt = await jobs.InspectAsync(request.Id);
        Assert.True(oldReceipt.Found); Assert.True(oldReceipt.Stopped); Assert.True(oldReceipt.Response!.Succeeded);
        await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        Assert.True((await jobs.InspectAsync(request.Id)).Response!.Succeeded); Assert.Empty(freshRunner.Requests);
    }

    [Fact]
    public async Task InterruptedDeployBeforeApplyProducesRetryableEvidenceAndRetainsIdempotency()
    {
        using var root = new AgentTestDirectory(); var runner = new BuildCancellation();
        var executor = AgentWorkExecutorTests.Executor(root.Path, runner);
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), executor, TimeProvider.System);
        var request = AgentWorkExecutorTests.Request("deploy"); await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var job = await jobs.InspectAsync(request.Id);
        Assert.Equal("interrupted_before_apply", job.Response!.Code); Assert.True(job.Stopped); Assert.False(job.ApplyStarted);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments.Contains("up"));
        await jobs.StartAsync(request); Assert.Equal(2, runner.Requests.Count);
    }
    [Fact]
    public async Task AgentOwnedDeadlinePersistsItsOwnCauseAndConfirmsInterruptionBeforeApply()
    {
        using var root = new AgentTestDirectory(); var runner = new DeadlineRunner();
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            AgentWorkExecutorTests.Executor(root.Path, runner), TimeProvider.System);
        var request = AgentWorkExecutorTests.Request("deploy") with { Policy = new(TimeoutSeconds: 1) };
        await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var result = await jobs.InspectAsync(request.Id);
        Assert.True(result.Stopped); Assert.False(result.ApplyStarted); Assert.Equal("deadline", result.CancellationReason);
        Assert.Equal("interrupted_before_apply", result.Response!.Code);
    }
    [Fact]
    public async Task FailedProcessCleanupNeverBecomesStoppedEvidenceEvenBeforeApply()
    {
        using var root = new AgentTestDirectory(); var runner = new CleanupFailure();
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            AgentWorkExecutorTests.Executor(root.Path, runner), TimeProvider.System);
        var request = AgentWorkExecutorTests.Request("deploy"); await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var result = await jobs.InspectAsync(request.Id);
        Assert.False(result.Stopped); Assert.Equal("uncertain", result.Response!.Code);
    }
    private sealed class DeadlineRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("unreachable"); }
    }
    private sealed class CleanupFailure : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => throw new ProcessTerminationException();
    }
    [Fact]
    public async Task BusyBeforeExecutorAdmissionCanBeRetriedButCompletedIdsRemainReplayOnly()
    {
        using var root = new AgentTestDirectory(); var executor = new BusyExecutor();
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), executor, TimeProvider.System);
        var request = AgentWorkExecutorTests.Request(); await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        Assert.Equal("agent_busy", (await jobs.InspectAsync(request.Id)).Response!.Code);
        await jobs.StartAsync(request);
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        Assert.True((await jobs.InspectAsync(request.Id)).Response!.Succeeded);
        await jobs.StartAsync(request); Assert.Equal(2, executor.Calls);
    }
    private sealed class BusyExecutor : IAgentWorkExecutor
    {
        public int Calls;
        public Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default) =>
            Task.FromResult(++Calls == 1 ? new AgentWorkResponse(request.Id, false, "agent_busy") : new(request.Id, true, "succeeded", ExecutionStopped: true));
    }
    private sealed class BuildCancellation : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("build")) throw new OperationCanceledException();
            return Task.FromResult(new ProcessResult(0, "{\"services\":{\"app\":{\"build\":\".\"}}}", "", false, false));
        }
    }
    private sealed class Executor(string phase) : IAgentWorkExecutor, IReportingAgentWorkExecutor
    {
        public event Func<Guid, string, Task>? Progress;
        public event Func<Guid, AgentOutputChunk, Task>? Output;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions;
        public async Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref Executions);
            if (Progress is { } report)
            { if (phase == "Checking") await report(request.Id, "Applying"); await report(request.Id, phase); }
            for (var seq = 1; seq <= 50; seq++) if (Output is { } write) await write(request.Id, new(seq, "stdout", "private-output\n"));
            Started.TrySetResult();
            await Finish.Task.WaitAsync(token);
            return new(request.Id, true, "succeeded", ExecutionStopped: true);
        }
    }
}
