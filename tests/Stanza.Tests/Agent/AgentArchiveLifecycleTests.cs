using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentArchiveLifecycleTests
{
    [Fact]
    public async Task VerifiedLegacyReceiptClearsUnresolvedStorageHealthWithoutReexecuting()
    {
        using var root = new AgentTestDirectory(); var id = Guid.NewGuid(); var hash = new string('a', 64);
        var directory = Path.Combine(root.Path, "jobs-v1"); Directory.CreateDirectory(directory);
        var job = new AgentJobInspection(id, true, true, false, "Building", false, 0, 0, 100);
        File.WriteAllText(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(new { Version = 1, Hash = hash, Job = job }));
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path }); var health = new AgentStorageHealth(options, new Disk());
        var executor = new Executor(); await using var jobs = new AgentJobStore(options, executor, TimeProvider.System, health: health);
        Assert.False(health.Read().Healthy); Assert.Equal(1, health.Read().UnresolvedJobs);
        var ledger = Path.Combine(root.Path, "request-ledger"); Directory.CreateDirectory(ledger);
        File.WriteAllText(Path.Combine(ledger, id.ToString("N") + ".json"), JsonSerializer.Serialize(new { Hash = hash,
            Response = new AgentWorkResponse(id, false, "interrupted_before_apply", ExecutionStopped: true, ApplyStarted: false) }));
        var inspected = await jobs.InspectAsync(id);
        Assert.True(inspected.Stopped); Assert.False(inspected.ApplyStarted);
        Assert.True(health.Read().Healthy); Assert.Equal(0, health.Read().UnresolvedJobs); Assert.Equal(0, executor.Executions);
    }

    [Fact]
    public async Task CompletedLegacyChunkHistoryRemainsReadableAndCanBeReleasedWithoutResettingReceipts()
    {
        using var root = new AgentTestDirectory(); var id = Guid.NewGuid();
        var directory = Path.Combine(root.Path, "jobs-v1"); Directory.CreateDirectory(directory);
        var job = new AgentJobInspection(id, true, false, true, "Checking", false, 2, 0, 100,
            Response: new(id, true, "succeeded", ExecutionStopped: true));
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        var original = JsonSerializer.Serialize(new { Version = 1, Hash = "old", Job = job }); File.WriteAllText(path, original);
        var chunks = Path.Combine(directory, id.ToString("N")); Directory.CreateDirectory(chunks);
        for (var seq = 1; seq <= 2; seq++) File.WriteAllText(Path.Combine(chunks, seq.ToString("D20") + ".json"), JsonSerializer.Serialize(new AgentOutputChunk(seq, "stdout", "legacy")));
        await using var jobs = new AgentJobStore(Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), new Executor(), TimeProvider.System);
        Assert.Equal(2, (await jobs.ReadOutputAsync(id, 0)).Count);
        Assert.Equal(original, File.ReadAllText(path)); // Merely reading an old installation never rewrites it.
        await jobs.CommitOutputAsync(id, 2);
        Assert.False(Directory.Exists(chunks)); Assert.True(File.Exists(path));
        Assert.True((await jobs.InspectAsync(id)).OutputArchived);
    }

    [Fact]
    public async Task TerminalPersistenceRetriesWithoutDiscardingTheOutcomeOrAllowingAnotherExecution()
    {
        using var root = new AgentTestDirectory(); var executor = new Executor(); var storage = new Storage(); var logs = new Logs();
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path });
        var health = new AgentStorageHealth(options, new Disk());
        await using var jobs = new AgentJobStore(options, executor, TimeProvider.System, logs, storage, health);
        var request = AgentWorkExecutorTests.Request("deploy");
        await jobs.StartAsync(request); await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        storage.FailTerminal = true; executor.Finish.TrySetResult();
        try
        {
            await storage.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await jobs.InspectAsync(request.Id);
            Assert.True(pending.Active); Assert.False(pending.Stopped); Assert.Null(pending.Response);
            Assert.Equal(1, health.Read().PendingTerminalResults); Assert.False(health.Read().Healthy);
            Assert.Null(await jobs.StartAsync(request));
            Assert.Equal("agent_busy", (await jobs.StartAsync(request with { Id = Guid.NewGuid() }))!.Code);
            Assert.Contains(logs.Errors, error => ReferenceEquals(error, storage.Error));
        }
        finally { storage.FailTerminal = false; }
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var completed = await jobs.InspectAsync(request.Id);
        Assert.True(completed.Stopped); Assert.True(completed.Response!.Succeeded);
        Assert.Equal(1, completed.Response.LastOutputSequence); Assert.Equal(1, executor.Executions);
        Assert.True(health.Read().Healthy);
    }

    [Fact]
    public async Task OutputIsTemporaryUntilFinalArchiveAcknowledgementAndSmallReceiptsSurviveRestart()
    {
        using var root = new AgentTestDirectory(); var executor = new Executor();
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path }); var request = AgentWorkExecutorTests.Request("deploy");
        var ledger = Path.Combine(root.Path, "request-ledger"); Directory.CreateDirectory(ledger);
        var receipt = Path.Combine(ledger, request.Id.ToString("N") + ".json"); File.WriteAllText(receipt, "keep-replay-evidence");
        await using (var jobs = new AgentJobStore(options, executor, TimeProvider.System))
        {
            await jobs.StartAsync(request); await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(await jobs.ReadOutputAsync(request.Id, 0));
            await Assert.ThrowsAsync<IOException>(() => jobs.CommitOutputAsync(request.Id, 1));
            executor.Finish.TrySetResult();
            await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
            await Assert.ThrowsAsync<IOException>(() => jobs.CommitOutputAsync(request.Id, 0));
            Assert.Single(await jobs.PendingArchivesAsync());
            await jobs.CommitOutputAsync(request.Id, 1); await jobs.CommitOutputAsync(request.Id, 1);
            Assert.Empty(await jobs.ReadOutputAsync(request.Id, 0)); Assert.Empty(await jobs.PendingArchivesAsync());
            Assert.False(File.Exists(Path.Combine(root.Path, "jobs-v1", request.Id.ToString("N") + ".output")));
            Assert.True(File.Exists(Path.Combine(root.Path, "jobs-v1", request.Id.ToString("N") + ".json")));
            Assert.Equal("keep-replay-evidence", File.ReadAllText(receipt));
        }
        var replacement = new Executor();
        await using var reopened = new AgentJobStore(options, replacement, TimeProvider.System);
        Assert.Null(await reopened.StartAsync(request));
        Assert.True((await reopened.InspectAsync(request.Id)).OutputArchived); Assert.Equal(0, replacement.Executions);
    }

    [Fact]
    public async Task OutputWriteFailureDoesNotEscapeIntoTheProcessAndHistoryIsMarkedIncomplete()
    {
        using var root = new AgentTestDirectory(); var executor = new Executor(pauseOutput: true);
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path }); var health = new AgentStorageHealth(options, new Disk());
        await using var jobs = new AgentJobStore(options, executor, TimeProvider.System, health: health);
        var request = AgentWorkExecutorTests.Request("deploy"); await jobs.StartAsync(request);
        await executor.BeforeOutput.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = Path.Combine(root.Path, "jobs-v1", request.Id.ToString("N") + ".output"); Directory.CreateDirectory(blocked);
        executor.AllowOutput.TrySetResult(); await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(health.Read().Healthy);
        executor.Finish.TrySetResult();
        await AgentTestWait.UntilAsync(async () => !(await jobs.InspectAsync(request.Id)).Active);
        var job = await jobs.InspectAsync(request.Id);
        Assert.True(job.Response!.Succeeded); Assert.True(job.Response.OutputIncomplete); Assert.True(job.Stopped);
        Assert.Equal(0, job.LastSequence);
        Directory.Delete(blocked);
        await jobs.CommitOutputAsync(request.Id, 0);
    }

    [Fact]
    public async Task LegacyUnfinishedEvidenceIsNotConvertedIntoStoppedEvidenceOrDeleted()
    {
        using var root = new AgentTestDirectory(); var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path });
        var id = Guid.NewGuid(); var directory = Path.Combine(root.Path, "jobs-v1"); Directory.CreateDirectory(directory);
        var job = new AgentJobInspection(id, true, true, false, "Building", false, 1, 1, 100);
        var json = JsonSerializer.Serialize(new { Version = 1, Hash = "old", Job = job });
        var path = Path.Combine(directory, id.ToString("N") + ".json"); File.WriteAllText(path, json);
        var chunks = Path.Combine(directory, id.ToString("N")); Directory.CreateDirectory(chunks);
        File.WriteAllText(Path.Combine(chunks, 1L.ToString("D20") + ".json"), JsonSerializer.Serialize(new AgentOutputChunk(1, "stdout", "kept")));
        File.WriteAllBytes(Path.Combine(chunks, 2L.ToString("D20") + ".json"), []);
        var health = new AgentStorageHealth(options, new Disk());
        await using var jobs = new AgentJobStore(options, new Executor(), TimeProvider.System, health: health);
        Assert.Single(await jobs.ReadOutputAsync(id, 0));
        await Assert.ThrowsAsync<IOException>(() => jobs.CommitOutputAsync(id, 1));
        Assert.Equal(json, File.ReadAllText(path)); Assert.Equal(2, Directory.GetFiles(chunks).Length);
        Assert.Equal(1, health.Read().UnresolvedJobs); Assert.False(health.Read().Healthy);
    }

    private sealed class Executor(bool pauseOutput = false) : IAgentWorkExecutor, IReportingAgentWorkExecutor
    {
        public event Func<Guid, string, Task>? Progress;
        public event Func<Guid, AgentOutputChunk, Task>? Output;
        public TaskCompletionSource BeforeOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions;
        public async Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        {
            Executions++;
            if (Progress is { } progress) await progress(request.Id, "Building");
            BeforeOutput.TrySetResult();
            if (pauseOutput) await AllowOutput.Task.WaitAsync(token);
            if (Output is { } output) await output(request.Id, new(1, "stdout", "temporary output"));
            Started.TrySetResult(); await Finish.Task.WaitAsync(token);
            return new(request.Id, true, "succeeded", ExecutionStopped: true);
        }
    }
    private sealed class Storage : IAgentJournalStorage
    {
        public volatile bool FailTerminal;
        public readonly IOException Error = new("simulated ENOSPC on terminal write");
        public readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AgentJournalStorage real = new();
        public void Write<T>(string path, T value, bool replace = true)
        {
            var json = JsonSerializer.SerializeToElement(value);
            if (FailTerminal && json.TryGetProperty("Job", out var job) && job.GetProperty("Response").ValueKind == JsonValueKind.Object)
            { Failed.TrySetResult(); throw Error; }
            real.Write(path, value, replace);
        }
    }
    private sealed class Disk : IAgentDiskSpace
    { public AgentDiskSpace Read(string path) => new(100L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024); }
    private sealed class Logs : ILogger<AgentJobStore>
    {
        public ConcurrentQueue<Exception> Errors = new();
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) Errors.Enqueue(exception); }
    }
}
