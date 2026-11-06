using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class OperationArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerAcknowledgesAgentCleanupOnlyAfterBothOutputAndResultAreSaved(bool databaseFails)
    {
        var archive = new WorkerArchive(); await using var h = await OperationTestHarness.CreateAsync(dispatcher: archive);
        archive.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", ExecutionStopped: true, LastOutputSequence: 2));
        archive.OnCommit = async id =>
        {
            await h.InScope(async services =>
            {
                var db = services.GetRequiredService<StanzaDbContext>();
                var row = await db.Set<OperationEntity>().SingleAsync(x => x.Id == id);
                Assert.Equal("Succeeded", row.State); Assert.NotNull(row.ProtectedResponse);
                Assert.Equal(2, await db.Set<OperationOutputEntity>().CountAsync(x => x.OperationId == id));
                return true;
            });
        };
        if (databaseFails) await h.InScope(async services =>
        {
            await services.GetRequiredService<StanzaDbContext>().Database.ExecuteSqlRawAsync("CREATE TRIGGER FailArchive BEFORE INSERT ON OperationOutput BEGIN SELECT RAISE(FAIL, 'simulated full database'); END;");
            return true;
        });
        var operation = await h.EnqueueAsync(); await h.StartWorkerAsync();
        await h.WaitStateAsync(operation.Id, databaseFails ? "Unknown" : "Succeeded");
        if (databaseFails) Assert.Equal(0, archive.Commits);
        else await archive.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunningHistoryComesFromAgentAndCompletedHistoryComesOnlyFromDatabase()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var operation = await h.EnqueueAsync(); var agent = new Archive();
        await h.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>();
            await db.Set<OperationEntity>().Where(x => x.Id == operation.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "Running"));
            var output = new OperationOutputStore(db, h.Protection, agent);
            Assert.Equal(new long[] { 1, 2 }, (await output.ReadAsync(operation.Id, 0)).Select(x => x.Sequence));
            Assert.Single(await output.ReadAsync(operation.Id, 1));
            Assert.Empty(await db.Set<OperationOutputEntity>().ToListAsync());
            var response = new AgentWorkResponse(operation.Id, true, "succeeded", ExecutionStopped: true, LastOutputSequence: 2);
            var job = new AgentJobInspection(operation.Id, true, false, true, "Checking", false, 2, 0, 100, Response: response);
            Assert.False(await output.CanReleaseAsync(h.Agent, job, default));
            await output.ArchiveAsync(h.Agent, operation.Id, 2, default);
            Assert.False(await output.CanReleaseAsync(h.Agent, job, default)); // Output alone is not a committed result.
            var row = await db.Set<OperationEntity>().SingleAsync(x => x.Id == operation.Id);
            row.State = "Succeeded"; row.ProtectedResponse = h.Protection.CreateProtector("Stanza.Operation.v1", operation.Id.ToString("N")).Protect(JsonSerializer.Serialize(response));
            await db.SaveChangesAsync();
            Assert.True(await output.CanReleaseAsync(h.Agent, job, default));
            Assert.False(await output.CanReleaseAsync(Guid.NewGuid(), job, default));
            Assert.False(await output.CanReleaseAsync(h.Agent, job with { LastSequence = 3 }, default));
            var reads = agent.Reads; agent.Offline = true;
            Assert.Equal(new long[] { 1, 2 }, (await output.ReadAsync(operation.Id, 0)).Select(x => x.Sequence));
            Assert.Equal(reads, agent.Reads);
            return true;
        });
    }

    [Fact]
    public async Task DatabaseWriteFailureKeepsArchiveUnacknowledgedAndCanResumeWithoutDuplicatingOutput()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var operation = await h.EnqueueAsync(); var agent = new Archive();
        await h.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>(); var output = new OperationOutputStore(db, h.Protection, agent);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailArchive BEFORE INSERT ON OperationOutput BEGIN SELECT RAISE(FAIL, 'simulated full database'); END;");
            try { await Assert.ThrowsAsync<DbUpdateException>(() => output.ArchiveAsync(h.Agent, operation.Id, 2, default)); }
            finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailArchive"); }
            Assert.Empty(await db.Set<OperationOutputEntity>().ToListAsync());
            Assert.Equal(0, agent.Commits);
            await output.AppendAsync(operation.Id, new(1, "stdout", "first\n")); // A previously committed archive prefix.
            await output.ArchiveAsync(h.Agent, operation.Id, 2, default);
            await output.ArchiveAsync(h.Agent, operation.Id, 2, default);
            Assert.Equal(2, await db.Set<OperationOutputEntity>().CountAsync());
            Assert.Equal(0, agent.Commits); // Caller must still persist the terminal operation result.
            return true;
        });
    }

    private sealed class Archive : IAgentOutputArchive
    {
        public int Reads, Commits; public bool Offline;
        public Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid agentId, Guid operationId, long after, int count, CancellationToken token = default)
        {
            Reads++;
            if (Offline) throw new AgentUnavailableException();
            return Task.FromResult<IReadOnlyList<AgentOutputChunk>>(new AgentOutputChunk[] { new(1, "stdout", "first\n"), new(2, "stderr", "second\n") }
                .Where(x => x.Sequence > after).Take(count).ToArray());
        }
        public Task CommitOutputAsync(Guid agentId, Guid operationId, long lastSequence, CancellationToken token = default)
        { Commits++; return Task.CompletedTask; }
        public Task WaitForOutputAsync(Guid operationId, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class WorkerArchive : OperationTestDispatcher, IAgentOutputArchive
    {
        public int Commits;
        public Func<Guid, Task>? OnCommit;
        public TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid agentId, Guid operationId, long after, int count, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<AgentOutputChunk>>(new AgentOutputChunk[] { new(1, "stdout", "one"), new(2, "stderr", "two") }.Where(x => x.Sequence > after).Take(count).ToArray());
        public async Task CommitOutputAsync(Guid agentId, Guid operationId, long lastSequence, CancellationToken token = default)
        {
            try { if (OnCommit is not null) await OnCommit(operationId); Commits++; Committed.TrySetResult(); }
            catch (Exception error) { Committed.TrySetException(error); throw; }
        }
        public Task WaitForOutputAsync(Guid operationId, CancellationToken token = default) => Task.Delay(100, token);
    }
}
