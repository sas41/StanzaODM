using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Runtime;

/// <summary>Agent-owned execution and durable replay. No socket or transport token owns a job.</summary>
public sealed class AgentJobStore : IAsyncDisposable
{
    private sealed record Journal(int Version, string Hash, AgentJobInspection Job);
    private sealed record ExecutorLedger(string Hash, AgentWorkResponse? Response);
    private readonly IAgentWorkExecutor executor;
    private readonly TimeProvider clock;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<Guid, (CancellationTokenSource Cancel, Task Work)> running = [];

    private readonly ILogger<AgentJobStore>? logger;
    public AgentJobStore(IOptions<AgentOptions> options, IAgentWorkExecutor executor, TimeProvider clock, ILogger<AgentJobStore>? logger = null)
    {
        this.executor = executor; this.clock = clock;
        this.logger = logger;
        root = PrivateStorage.EnsureDirectory(Path.Combine(options.Value.PersistentDataRoot, "jobs-v1"));
        if (executor is IReportingAgentWorkExecutor reporting)
        { reporting.Progress += ProgressAsync; reporting.Output += OutputAsync; }
    }

    private string PathFor(Guid id) => Path.Combine(root, id.ToString("N") + ".json");
    private Journal? Read(Guid id)
    {
        var path = PathFor(id); PrivateStorage.RejectLinks(path);
        if (!File.Exists(path)) return null;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new IOException("Invalid job journal.");
        if (journal.Version != 1 || journal.Job.Id != id) throw new IOException("Unsupported job journal.");
        return journal;
    }
    private void Write(Journal journal)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = PathFor(journal.Job.Id); PrivateStorage.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, UnixCreateMode = PrivateStorage.FileMode }))
            { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
            File.Move(temporary, path, true); AgentWorkExecutor.FlushDirectory(root);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<AgentWorkResponse?> StartAsync(AgentWorkRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonSerializerOptions.Web)));
            var previous = Read(request.Id);
            if (previous is not null)
            {
                if (previous.Hash != hash) return new(request.Id, false, "request_id_conflict");
                // Busy is a pre-admission rejection from the executor's exclusive process lease,
                // not accepted execution. Every other accepted ID remains replay-only forever.
                if (previous.Job.Response?.Code != "agent_busy") return null;
            }
            // The original executor ledger remains authoritative for requests accepted before this upgrade.
            // ExecuteAsync consults it and never repeats an accepted command.
            if (running.Count > 0) return new(request.Id, false, "agent_busy");
            if (Directory.EnumerateFiles(root, "*.json").Take(100001).Count() >= 100000)
                return new(request.Id, false, "ledger_capacity");
            var job = new AgentJobInspection(request.Id, true, true, false, "Accepted", false, 0, 0, Now);
            Write(new(1, hash, job));
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            running.Add(request.Id, (cancel, Task.Run(() => ExecuteAsync(request, cancel.Token))));
            return null;
        }
        finally { gate.Release(); }
    }
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private async Task ExecuteAsync(AgentWorkRequest request, CancellationToken token)
    {
        using var heartbeatLifetime = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(request.Id, heartbeatLifetime.Token);
        AgentWorkResponse response;
        try { response = await executor.ExecuteAsync(request, token); }
        catch (Exception error)
        {
            logger?.LogError("Agent job {OperationId} failed without a terminal execution result ({ErrorType}).", request.Id, error.GetType().Name);
            response = new(request.Id, false, "uncertain");
        }
        await heartbeatLifetime.CancelAsync();
        await heartbeat;
        await gate.WaitAsync();
        try
        {
            var journal = Read(request.Id)!;
            // An uncertain executor result does not prove process cleanup, even if its Task returned.
            var stopped = response.ExecutionStopped || response.Succeeded;
            if (response.Code == "interrupted_before_apply") stopped = true;
            var reason = journal.Job.CancellationReason ?? response.CancellationReason;
            Write(journal with { Job = journal.Job with { Active = false, Stopped = stopped,
                Response = response with { CancellationReason = reason, ApplyStarted = journal.Job.ApplyStarted }, CancellationReason = reason, LastHeartbeat = Now } });
        }
        finally
        {
            if (running.Remove(request.Id, out var active)) active.Cancel.Dispose();
            gate.Release();
        }
    }
    private async Task HeartbeatAsync(Guid id, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, ct);
                await gate.WaitAsync(ct);
                try { var journal = Read(id)!; Write(journal with { Job = journal.Job with { LastHeartbeat = Now } }); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger?.LogError("Agent job {OperationId} heartbeat persistence failed ({ErrorType}); existing execution evidence is retained.", id, error.GetType().Name); }
    }
    private async Task ProgressAsync(Guid id, string phase)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id)!;
            Write(journal with { Job = journal.Job with { Phase = phase,
                ApplyStarted = journal.Job.ApplyStarted || phase == "Applying", LastHeartbeat = Now } });
        }
        finally { gate.Release(); }
    }
    private async Task OutputAsync(Guid id, AgentOutputChunk chunk)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id)!;
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var directory = PrivateStorage.EnsureDirectory(Path.Combine(root, id.ToString("N")));
            var path = Path.Combine(directory, chunk.Sequence.ToString("D20") + ".json"); PrivateStorage.RejectLinks(path);
            using (var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, UnixCreateMode = PrivateStorage.FileMode }))
            { JsonSerializer.Serialize(stream, chunk); stream.Flush(true); }
            AgentWorkExecutor.FlushDirectory(directory);
            Write(journal with { Job = journal.Job with { LastSequence = chunk.Sequence, LastHeartbeat = Now } });
        }
        finally { gate.Release(); }
    }
    public async Task<AgentJobInspection> InspectAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id);
            if (!running.ContainsKey(id) && journal?.Job.Stopped != true)
            {
                // Upgrade old terminal receipts, and recover the crash window between the
                // executor's fsynced receipt and this store's final journal update. An unfinished
                // legacy ledger is never converted into stopped-process evidence.
                var legacyPath = Path.Combine(Path.GetDirectoryName(root)!, "request-ledger", id.ToString("N") + ".json");
                PrivateStorage.RejectLinks(legacyPath);
                if (File.Exists(legacyPath))
                {
                    var legacy = JsonSerializer.Deserialize<ExecutorLedger>(File.ReadAllText(legacyPath));
                    if (legacy?.Response is { } receipt && receipt.Id == id && (receipt.Succeeded || receipt.ExecutionStopped) &&
                        (journal is null || string.Equals(legacy.Hash, journal.Hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        var job = journal?.Job ?? new AgentJobInspection(id, true, false, false, "Checking", true, 0, 0, null);
                        journal = new(1, legacy.Hash.ToUpperInvariant(), job with { Active = false, Stopped = true, Response = receipt,
                            ApplyStarted = job.ApplyStarted || receipt.ApplyStarted, CancellationReason = job.CancellationReason ?? receipt.CancellationReason });
                        Write(journal);
                    }
                }
            }
            if (journal is null) return new(id, false, false, false, "Unavailable", false, 0, 0, null);
            // After a process/container restart, an unfinished journal is evidence of uncertainty,
            // not evidence that an orphaned CLI or daemon-side operation stopped.
            return journal.Job with { Active = running.ContainsKey(id) };
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid id, long after)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id); var chunks = new List<AgentOutputChunk>();
            for (var seq = after + 1; seq <= (journal?.Job.LastSequence ?? 0) && chunks.Count < 64; seq++)
            {
                var path = Path.Combine(root, id.ToString("N"), seq.ToString("D20") + ".json"); PrivateStorage.RejectLinks(path);
                chunks.Add(JsonSerializer.Deserialize<AgentOutputChunk>(File.ReadAllText(path)) ?? throw new IOException("Invalid output journal."));
            }
            return chunks;
        }
        finally { gate.Release(); }
    }
    public async Task AcknowledgeAsync(Guid id, long sequence)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id) ?? throw new IOException("Unknown job acknowledgement.");
            if (sequence < 0 || sequence > journal.Job.LastSequence) throw new IOException("Invalid output acknowledgement.");
            if (sequence > journal.Job.AcknowledgedSequence)
                Write(journal with { Job = journal.Job with { AcknowledgedSequence = sequence } });
        }
        finally { gate.Release(); }
    }
    public async Task CancelAsync(Guid id, string reason)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id);
            if (journal is null || !running.TryGetValue(id, out var active)) return;
            try
            {
                if (journal.Job.CancellationReason is null) Write(journal with { Job = journal.Job with { CancellationReason = reason } });
            }
            finally { active.Cancel.Cancel(); } // Storage failure must never disable explicit cancellation.
        }
        finally { gate.Release(); }
    }
    public async Task CancelAllAsync(string reason)
    {
        Guid[] ids; await gate.WaitAsync();
        try { ids = running.Keys.ToArray(); } finally { gate.Release(); }
        foreach (var id in ids) await CancelAsync(id, reason);
    }
    public async Task RecordDisconnectAsync(string reason, int? closeStatus, IReadOnlyCollection<Guid>? operationIds = null)
    {
        await gate.WaitAsync();
        try
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var path = Path.Combine(root, "disconnects.jsonl"); PrivateStorage.RejectLinks(path);
            using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, UnixCreateMode = PrivateStorage.FileMode });
            var jobs = running.Keys.Concat(operationIds ?? []).Distinct().Select(Read).Where(x => x is not null)
                .Select(x => x!.Job with { Response = null, Active = running.ContainsKey(x.Job.Id) }).ToArray();
            JsonSerializer.Serialize(stream, new { At = Now, Reason = reason, CloseStatus = closeStatus, Jobs = jobs });
            stream.WriteByte((byte)'\n'); stream.Flush(true);
            AgentWorkExecutor.FlushDirectory(root);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await CancelAllAsync("shutdown");
        Task[] tasks; await gate.WaitAsync();
        try { tasks = running.Values.Select(x => x.Work).ToArray(); } finally { gate.Release(); }
        await Task.WhenAll(tasks);
        if (executor is IReportingAgentWorkExecutor reporting)
        { reporting.Progress -= ProgressAsync; reporting.Output -= OutputAsync; }
        shutdown.Dispose();
    }
}
