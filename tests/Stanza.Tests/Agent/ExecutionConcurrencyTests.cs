using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class ExecutionConcurrencyTests
{
    [Fact]
    public async Task UnresolvedWorkKeepsItsSlotAndDeploymentAcrossRestartWithoutBlockingDiscovery()
    {
        using var root = new AgentTestDirectory(); var deployment = Guid.NewGuid();
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path });
        var unknown = AgentWorkExecutorTests.Request("deploy") with { DeploymentId = deployment };
        await using (var jobs = new AgentJobStore(options, new UnresolvedExecutor(deployment), TimeProvider.System))
        {
            await jobs.ConfigureAsync(new(MaximumParallelOperations: 2));
            Assert.Null(await jobs.StartAsync(unknown));
            await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(unknown.Id)).Active);
            Assert.False((await jobs.InspectAsync(unknown.Id)).Stopped);
        }
        await using var restarted = new AgentJobStore(options, new UnresolvedExecutor(deployment), TimeProvider.System);
        await restarted.ConfigureAsync(new(MaximumParallelOperations: 2));
        Assert.Equal("agent_busy", (await restarted.StartAsync(unknown with { Id = Guid.NewGuid() }))!.Code);
        var independent = unknown with { Id = Guid.NewGuid(), DeploymentId = Guid.NewGuid() };
        Assert.Null(await restarted.StartAsync(independent));
        await AgentTestWait.UntilAsync(async () => !(await restarted.InspectAsync(independent.Id)).Active);
        await restarted.ConfigureAsync(new(MaximumParallelOperations: 1));
        Assert.Equal("agent_busy", (await restarted.StartAsync(independent with { Id = Guid.NewGuid() }))!.Code);
        var query = independent with { Id = Guid.NewGuid(), Operation = "git-branches" };
        Assert.Null(await restarted.StartAsync(query));
        await AgentTestWait.UntilAsync(async () => !(await restarted.InspectAsync(query.Id)).Active);
        Assert.True((await restarted.InspectAsync(query.Id)).Response!.Succeeded);
    }

    [Fact]
    public async Task AnUnreadableExecutionJournalFailsClosedForWorkloadsWithoutDisablingDiscovery()
    {
        using var root = new AgentTestDirectory(); var path = Path.Combine(root.Path, "jobs-v1"); Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, Guid.NewGuid().ToString("N") + ".json"), "{broken");
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), new UnresolvedExecutor(Guid.Empty), TimeProvider.System);
        Assert.Equal("agent_busy", (await jobs.StartAsync(AgentWorkExecutorTests.Request("deploy")))!.Code);
        var query = AgentWorkExecutorTests.Request("git-branches"); Assert.Null(await jobs.StartAsync(query));
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(query.Id)).Active);
        Assert.True((await jobs.InspectAsync(query.Id)).Response!.Succeeded);
    }

    private sealed class UnresolvedExecutor(Guid deployment) : IAgentWorkExecutor
    {
        public Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default) =>
            Task.FromResult(request.DeploymentId == deployment && !ExecutionScheduling.IsDiscovery(request.Operation)
                ? new AgentWorkResponse(request.Id, false, "uncertain", ExecutionStopped: false, ApplyStarted: true)
                : new AgentWorkResponse(request.Id, true, "succeeded", ExecutionStopped: true));
    }

    [Fact]
    public async Task NetworkDiscoveryDoesNotAccumulateWorkloadReceiptsArchivesOrScratchFiles()
    {
        using var root = new AgentTestDirectory();
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => new(0,
            p.Executable == "/usr/bin/git" ? new string('a', 40) + "\trefs/heads/main\n" : "", "", false, false) };
        var executor = AgentWorkExecutorTests.Executor(root.Path, runner);
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), executor, TimeProvider.System);
        for (var i = 0; i < 5; i++)
        {
            var request = AgentWorkExecutorTests.Request("git-action-branches") with { Sources = [new("https://git.example.test/app", "main", "source")] };
            Assert.Null(await jobs.StartAsync(request));
            await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
            Assert.True((await jobs.InspectAsync(request.Id)).Response!.Succeeded);
            Assert.True(await jobs.ForgetDiscoveryAsync(request.Id));
            Assert.False((await jobs.InspectAsync(request.Id)).Found);
        }
        Assert.False(Directory.Exists(Path.Combine(root.Path, "request-ledger")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root.Path, "jobs-v1"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(root.Path, "discovery"), "*", SearchOption.AllDirectories));
        Assert.Empty(await jobs.PendingArchivesAsync());
        var work = AgentWorkExecutorTests.Request("docker-script") with { Execution = new("/bin/bash", [], Mode: "script", Script: "echo ready") };
        Assert.Null(await jobs.StartAsync(work));
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(work.Id)).Active);
        Assert.True((await jobs.InspectAsync(work.Id)).Response!.Succeeded);
        Assert.False(await jobs.ForgetDiscoveryAsync(work.Id));
        Assert.True(File.Exists(Path.Combine(root.Path, "request-ledger", work.Id.ToString("N") + ".json")));
    }

    [Fact]
    public void SharedAdmissionRulesSeparateCapacityDeploymentsAndExclusiveHostWork()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var policy = new AgentRuntimePolicy(MaximumParallelOperations: 2, MaximumParallelDiscovery: 1);
        ExecutionSlot[] active = [new(a, "deploy")];
        Assert.True(ExecutionScheduling.CanAdmit(policy, new(b, "deploy"), active));
        Assert.False(ExecutionScheduling.CanAdmit(policy, new(a, "restart"), active));
        Assert.False(ExecutionScheduling.CanAdmit(new(), new(b, "deploy"), active));
        Assert.True(ExecutionScheduling.CanAdmit(policy, new(a, "git-action-branches"), active));
        Assert.False(ExecutionScheduling.CanAdmit(policy, new(b, "host-exec"), active));
        Assert.False(ExecutionScheduling.CanAdmit(policy, new(b, "deploy"), [new(a, "docker-script")]));
        Assert.True(ExecutionScheduling.CanAdmit(policy, new(a, "git-branches"), [new(a, "docker-script")]));
        Assert.False(ExecutionScheduling.CanAdmit(policy, new(b, "registry-tags"), [new(a, "git-branches")]));
        Assert.True(ExecutionScheduling.CanAdmit(policy, new(b, "deploy"), [new(a, "git-branches")]));
    }

    [Theory]
    [InlineData(0, 4, 60)][InlineData(65, 4, 60)][InlineData(1, 0, 60)][InlineData(1, 65, 60)][InlineData(1, 4, 0)][InlineData(1, 4, 604801)]
    public void InvalidCapacityCannotAdmitRequests(int workloads, int discovery, int timeout)
    {
        var policy = new AgentRuntimePolicy(MaximumParallelOperations: workloads, MaximumParallelDiscovery: discovery, DiscoveryTimeoutSeconds: timeout);
        Assert.False(ExecutionScheduling.IsValid(policy));
        Assert.False(ExecutionScheduling.CanAdmit(policy, new(Guid.NewGuid(), "deploy"), []));
    }

    [Fact]
    public async Task AgentEnforcesConfiguredWorkloadAndDiscoveryLimitsRatherThanCallerSuppliedLimits()
    {
        using var root = new AgentTestDirectory(); var executor = new HoldingExecutor();
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), executor, TimeProvider.System);
        var first = AgentWorkExecutorTests.Request("deploy") with { Policy = new(MaximumParallelOperations: 64) };
        Assert.Null(await jobs.StartAsync(first));
        Assert.Equal("agent_busy", (await jobs.StartAsync(first with { Id = Guid.NewGuid(), DeploymentId = Guid.NewGuid() }))!.Code);
        await jobs.ConfigureAsync(new(MaximumParallelOperations: 2, MaximumParallelDiscovery: 1));
        Assert.Equal("agent_busy", (await jobs.StartAsync(first with { Id = Guid.NewGuid() }))!.Code);
        var second = first with { Id = Guid.NewGuid(), DeploymentId = Guid.NewGuid() };
        Assert.Null(await jobs.StartAsync(second));
        Assert.Equal("agent_busy", (await jobs.StartAsync(first with { Id = Guid.NewGuid(), DeploymentId = Guid.NewGuid() }))!.Code);
        var discovery = first with { Id = Guid.NewGuid(), Operation = "git-action-branches" };
        Assert.Null(await jobs.StartAsync(discovery));
        Assert.Equal("agent_busy", (await jobs.StartAsync(discovery with { Id = Guid.NewGuid() }))!.Code);
        Assert.Equal("agent_busy", (await jobs.StartAsync(first with { Id = Guid.NewGuid(), Operation = "docker-script", DeploymentId = Guid.Empty }))!.Code);
        await executor.WaitForCount(3);
        await jobs.ConfigureAsync(new(MaximumParallelOperations: 1));
        Assert.True((await jobs.InspectAsync(first.Id)).Active); Assert.True((await jobs.InspectAsync(second.Id)).Active);
        executor.Finish.TrySetResult();
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(first.Id)).Active && !(await jobs.InspectAsync(second.Id)).Active && !(await jobs.InspectAsync(discovery.Id)).Active);
        Assert.Null(await jobs.StartAsync(first)); Assert.Equal(3, executor.Requests.Count);
    }

    [Fact]
    public async Task ParallelExecutorCallsKeepOutputSequencesApplyEvidenceAndLiveCredentialsIsolated()
    {
        using var root = new AgentTestDirectory(); var runner = new HoldingRunner();
        var executor = AgentWorkExecutorTests.Executor(root.Path, runner);
        var output = new ConcurrentDictionary<Guid, ConcurrentQueue<AgentOutputChunk>>();
        executor.Output += (id, chunk) => { output.GetOrAdd(id, _ => new()).Enqueue(chunk); return Task.CompletedTask; };
        AgentWorkRequest Request() => AgentWorkExecutorTests.Request("docker-script") with { DeploymentId = Guid.NewGuid(), Files = [], EnvironmentFiles = [],
            Execution = new("/bin/bash", [], Mode: "script", Script: "echo isolated") };
        var a = Request(); var b = Request();
        var first = executor.ExecuteAsync(a); await runner.WaitForCount(1);
        var metadata = await executor.ExecuteAsync(a with { Id = Guid.NewGuid(), Operation = "git-action-branches", Execution = null,
            Sources = [new("https://git.example.test/app", "main", "source")] }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(metadata.Succeeded, metadata.Code); Assert.Contains("main", metadata.Output); Assert.False(metadata.ApplyStarted); Assert.False(first.IsCompleted);
        var second = executor.ExecuteAsync(b); await runner.WaitForCount(2);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "credentials", a.Id.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "credentials", b.Id.ToString("N"))));
        runner.Finish.TrySetResult();
        foreach (var result in await Task.WhenAll(first, second)) { Assert.True(result.Succeeded, result.Code); Assert.True(result.ApplyStarted); }
        Assert.Equal(new long[] { 1, 2 }, output[a.Id].Select(c => c.Sequence));
        Assert.Equal(new long[] { 1, 2 }, output[b.Id].Select(c => c.Sequence));
        Assert.All(output[a.Id], c => Assert.Contains(a.Id.ToString("N"), c.Text));
        Assert.All(output[b.Id], c => Assert.Contains(b.Id.ToString("N"), c.Text));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Path, "credentials")));
    }

    private sealed class HoldingExecutor : IAgentWorkExecutor
    {
        public readonly ConcurrentDictionary<Guid, AgentWorkRequest> Requests = new();
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        { Requests[request.Id] = request; await Finish.Task.WaitAsync(token); return new(request.Id, true, "succeeded", ExecutionStopped: true); }
        public async Task WaitForCount(int count) => await AgentTestWait.UntilAsync(() => Requests.Count >= count);
    }
    private sealed class HoldingRunner : IProcessRunner
    {
        private readonly ConcurrentDictionary<string, byte> active = new();
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Executable == "/usr/bin/git") return new(0, new string('a', 40) + "\trefs/heads/main\n", "", false, false);
            var id = Path.GetFileName(request.WorkingDirectory);
            if (request.OnOutput is { } output) await output("stdout", id + "-start\n");
            active[id] = 0;
            await Finish.Task.WaitAsync(cancellationToken);
            Assert.True(File.Exists(Path.Combine(request.Environment["DOCKER_CONFIG"], "config.json")));
            if (request.OnOutput is { } final) await final("stdout", id + "-end\n");
            return new(0, "", "", false, false);
        }
        public async Task WaitForCount(int count) => await AgentTestWait.UntilAsync(() => active.Count >= count);
    }
}
