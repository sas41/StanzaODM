using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Core.Contracts;
using Stanza.Agent.Transport;

namespace Stanza.Tests.Agent;

public sealed class EnrollmentWorkerTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(10000, 60)]
    public void ExponentialBackoffIsBounded(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), EnrollmentWorker.RetryDelay(failures));

    [Fact]
    public async Task RetriesThenPendingThenApprovedStillCannotExecuteAndResetsBackoff()
    {
        using var directory = new AgentTestDirectory();
        var options = AgentOptionsTests.ValidOptions();
        options.PersistentDataRoot = directory.Path;
        var clock = new ManualTimeProvider();
        var client = new FakeClient(EnrollmentStatus.Retrying, EnrollmentStatus.Retrying,
            EnrollmentStatus.Pending, EnrollmentStatus.Approved, EnrollmentStatus.Retrying);
        var state = new AgentState();
        using var worker = new EnrollmentWorker(Options.Create(options), client, state, clock, NullLogger<EnrollmentWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            foreach (var expected in new[]
            {
                (EnrollmentStatus.Retrying, 2), (EnrollmentStatus.Retrying, 4),
                (EnrollmentStatus.Pending, 30), (EnrollmentStatus.Approved, 30), (EnrollmentStatus.Retrying, 2)
            })
            {
                var timer = await clock.NextTimerAsync();
                Assert.Equal(TimeSpan.FromSeconds(expected.Item2), timer.DueTime);
                Assert.Equal(expected.Item1, state.Snapshot.Enrollment);
                Assert.Equal(expected.Item1 != EnrollmentStatus.Approved, state.Snapshot.Ready);
                Assert.False(state.Snapshot.CanExecuteWork);
                Assert.False(state.Snapshot.CommandChannelAvailable);
                if (client.Requests.Count < 5) { clock.Advance(timer); }
            }

            Assert.Equal(5, client.Requests.Count);
            Assert.Equal(5, client.Requests.Select(request => request.Nonce).Distinct().Count());
            Assert.All(client.Requests, request => Assert.True(AgentEnrollmentProtocol.VerifyProof(request, clock.GetUtcNow())));
        }
        finally { await worker.StopAsync(default); }
        Assert.False(state.Snapshot.Ready);
    }

    [Theory]
    [InlineData(EnrollmentStatus.Rejected)]
    [InlineData(EnrollmentStatus.Revoked)]
    [InlineData(EnrollmentStatus.Incompatible)]
    public async Task TerminalEnrollmentStatusHaltsWithoutExecution(EnrollmentStatus status)
    {
        using var directory = new AgentTestDirectory();
        var options = AgentOptionsTests.ValidOptions();
        options.PersistentDataRoot = directory.Path;
        var client = new FakeClient(status);
        var state = new AgentState();
        var clock = new ManualTimeProvider();
        using var worker = new EnrollmentWorker(Options.Create(options), client, state, clock, NullLogger<EnrollmentWorker>.Instance);
        await worker.StartAsync(default);
        await AgentTestWait.UntilAsync(() => state.Snapshot.Enrollment == status);
        Assert.Equal(status, state.Snapshot.Enrollment);
        Assert.Single(client.Requests);
        Assert.False(state.Snapshot.CanExecuteWork);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task InvalidStorageIsNotReadyAndDoesNotRegister()
    {
        using var directory = new AgentTestDirectory();
        var path = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(path, "file");
        var options = AgentOptionsTests.ValidOptions();
        options.PersistentDataRoot = path;
        var client = new FakeClient(EnrollmentStatus.Pending);
        var state = new AgentState();
        using var worker = new EnrollmentWorker(Options.Create(options), client, state, TimeProvider.System, NullLogger<EnrollmentWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EnrollmentStatus.Unavailable, state.Snapshot.Enrollment);
        Assert.False(state.Snapshot.Ready);
        Assert.Empty(client.Requests);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task IncompatibleChannelHaltsWithoutPretendingApprovedMeansReady()
    {
        using var directory = new AgentTestDirectory();
        var options = AgentOptionsTests.ValidOptions(); options.PersistentDataRoot = directory.Path;
        var client = new FakeClient(EnrollmentStatus.Approved); var state = new AgentState();
        using var worker = new EnrollmentWorker(Options.Create(options), client, state, TimeProvider.System,
            NullLogger<EnrollmentWorker>.Instance, new IncompatibleChannel());
        await worker.StartAsync(default);
        await AgentTestWait.UntilAsync(() => state.Snapshot.Enrollment == EnrollmentStatus.Incompatible);
        Assert.False(state.Snapshot.Ready); Assert.False(state.Snapshot.CanExecuteWork); Assert.Single(client.Requests);
        await worker.StopAsync(default);
    }
    private sealed class IncompatibleChannel : IAgentCommandChannel
    {
        public Task RunAsync(AgentIdentity identity, CancellationToken token) => throw new AgentChannelProtocolException();
    }

    private sealed class FakeClient(params EnrollmentStatus[] statuses) : IEnrollmentClient
    {
        public ConcurrentQueue<AgentEnrollmentRequest> Requests { get; } = new();
        public Task<EnrollmentStatus> RegisterAsync(Uri controlPlane, AgentEnrollmentRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(statuses[Math.Min(Requests.Count - 1, statuses.Length - 1)]);
        }
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly ConcurrentQueue<Timer> timers = new();
        private readonly SemaphoreSlim created = new(0);
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, dueTime);
            timers.Enqueue(timer);
            created.Release();
            return timer;
        }
        public async Task<Timer> NextTimerAsync()
        {
            Assert.True(await created.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(timers.TryDequeue(out var timer));
            return timer;
        }
        public void Advance(Timer timer)
        {
            now += timer.DueTime;
            timer.Fire();
        }
        public sealed class Timer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            public TimeSpan DueTime { get; } = dueTime;
            private bool disposed;
            public void Fire() { if (!disposed) { callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
