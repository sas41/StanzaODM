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
    private readonly Dictionary<Guid, Journal> journals = [];
    private readonly Dictionary<Guid, AgentOutputSpool> spools = [];
    private readonly HashSet<Guid> outputFailures = [];
    private readonly IAgentJournalStorage storage;
    private readonly AgentStorageHealth? health;
    private readonly CancellationTokenSource persistenceStopping = new();
    private int archiveCursor;

    private readonly ILogger<AgentJobStore>? logger;
    public AgentJobStore(IOptions<AgentOptions> options, IAgentWorkExecutor executor, TimeProvider clock, ILogger<AgentJobStore>? logger = null,
        IAgentJournalStorage? storage = null, AgentStorageHealth? health = null)
    {
        this.executor = executor; this.clock = clock;
        this.logger = logger;
        this.storage = storage ?? new AgentJournalStorage(); this.health = health;
        root = PrivateStorage.EnsureDirectory(Path.Combine(options.Value.PersistentDataRoot, "jobs-v1"));
        if (executor is IReportingAgentWorkExecutor reporting)
        { reporting.Progress += ProgressAsync; reporting.Output += OutputAsync; }
        if (health is not null)
            foreach (var path in Directory.EnumerateFiles(root, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                {
                    try { if (Read(id)?.Job.Stopped != true) health.Unresolved(id); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                    { Failed(id, "journal-read", error); }
                    finally { journals.Remove(id); }
                }
    }

    private string PathFor(Guid id) => Path.Combine(root, id.ToString("N") + ".json");
    private Journal? Read(Guid id)
    {
        if (journals.TryGetValue(id, out var cached)) return cached;
        var path = PathFor(id); PrivateStorage.RejectLinks(path);
        if (!File.Exists(path)) return null;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new IOException("Invalid job journal.");
        if (journal.Version is not (1 or 2) || journal.Job.Id != id) throw new IOException("Unsupported job journal.");
        journals[id] = journal;
        return journal;
    }
    private void Write(Journal journal)
    {
        try
        {
            storage.Write(PathFor(journal.Job.Id), journal);
            journals[journal.Job.Id] = journal;
            health?.Succeeded(journal.Job.Id, "journal");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Failed(journal.Job.Id, "journal", error); throw; }
    }

    private void Failed(Guid id, string stage, Exception error)
    {
        if (health?.Failed(id, stage, error) != false)
            logger?.LogError(error, "Agent job {OperationId} {Stage} persistence failed (I/O code {ErrorCode}).", id, stage, error.HResult);
    }
    private string SpoolPath(Guid id) => Path.Combine(root, id.ToString("N") + ".output");
    private AgentOutputSpool Spool(Guid id)
    {
        if (!spools.TryGetValue(id, out var spool)) spools[id] = spool = new(SpoolPath(id));
        return spool;
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
            if (health?.Read() is { AvailableBytes: { } available, MinimumFreeBytes: var minimum } && available < minimum)
                return new(request.Id, false, "agent_storage_low");
            if (Directory.EnumerateFiles(root, "*.json").Take(100001).Count() >= 100000)
                return new(request.Id, false, "ledger_capacity");
            var job = new AgentJobInspection(request.Id, true, true, false, "Accepted", false, 0, 0, Now);
            Write(new(2, hash, job));
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
            logger?.LogError(error, "Agent job {OperationId} failed without a terminal execution result.", request.Id);
            response = new(request.Id, false, "uncertain");
        }
        await heartbeatLifetime.CancelAsync();
        await heartbeat;
        health?.Pending(request.Id);
        while (true)
        {
            await gate.WaitAsync();
            try
            {
                var journal = Read(request.Id)!;
                if (journal.Version == 2) Spool(request.Id).FlushDurable();
                // Retain this actual executor outcome in the task until persistence succeeds.
                var stopped = response.ExecutionStopped || response.Succeeded || response.Code == "interrupted_before_apply";
                var reason = journal.Job.CancellationReason ?? response.CancellationReason;
                var terminal = response with { CancellationReason = reason, ApplyStarted = journal.Job.ApplyStarted || response.ApplyStarted,
                    OutputIncomplete = response.OutputIncomplete || outputFailures.Contains(request.Id) || journal.Version == 2 && Spool(request.Id).Incomplete,
                    LastOutputSequence = journal.Job.LastSequence };
                Write(journal with { Job = journal.Job with { Active = false, Stopped = stopped,
                    Response = terminal, ApplyStarted = terminal.ApplyStarted, CancellationReason = reason, LastHeartbeat = Now } });
                if (running.Remove(request.Id, out var active)) active.Cancel.Dispose();
                health?.Durable(request.Id);
                if (stopped) health?.Resolved(request.Id); else health?.Unresolved(request.Id);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Failed(request.Id, "terminal", error);
                // Neither a transport failure nor a full disk is permission to discard this result.
                if (persistenceStopping.IsCancellationRequested) return;
            }
            finally { gate.Release(); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, persistenceStopping.Token); }
            catch (OperationCanceledException) { return; }
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
                try
                {
                    var journal = Read(id)!;
                    if (journal.Version == 2) Spool(id).FlushDurable();
                    Write(journal with { Job = journal.Job with { LastHeartbeat = Now } });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Failed(id, "heartbeat", error); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task ProgressAsync(Guid id, string phase)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id)!;
            if (journal.Version == 2) Spool(id).FlushDurable();
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
            if (outputFailures.Contains(id)) return;
            var journal = Read(id)!;
            try
            {
                Spool(id).Append(chunk);
                // The live cursor is memory-only; phase checkpoints/heartbeats/finalization fsync it.
                journals[id] = journal with { Job = journal.Job with { LastSequence = chunk.Sequence, LastHeartbeat = Now } };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                outputFailures.Add(id); Failed(id, "output", error);
                // Output loss is reported with the result, not thrown into the process's stdout reader.
            }
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
            if (!running.ContainsKey(id))
            {
                if (journal.Job.Stopped) health?.Resolved(id); else health?.Unresolved(id);
            }
            // After a process/container restart, an unfinished journal is evidence of uncertainty,
            // not evidence that an orphaned CLI or daemon-side operation stopped.
            return journal.Job with { Active = running.ContainsKey(id) };
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<AgentOutputChunk>> ReadOutputAsync(Guid id, long after, int count = 64)
    {
        await gate.WaitAsync();
        try
        {
            if (after < 0 || count is < 1 or > 64) throw new IOException("Invalid output cursor.");
            var journal = Read(id); var chunks = new List<AgentOutputChunk>();
            if (journal is null || journal.Job.OutputArchived) return chunks;
            if (journal.Version == 2) return Spool(id).Read(after, count);
            for (var seq = after + 1; seq <= journal.Job.LastSequence && chunks.Count < count; seq++)
            {
                var path = Path.Combine(root, id.ToString("N"), seq.ToString("D20") + ".json"); PrivateStorage.RejectLinks(path);
                chunks.Add(JsonSerializer.Deserialize<AgentOutputChunk>(File.ReadAllText(path)) ?? throw new IOException("Invalid output journal."));
            }
            return chunks;
        }
        finally { gate.Release(); }
    }
    public async Task CommitOutputAsync(Guid id, long lastSequence)
    {
        await gate.WaitAsync();
        try
        {
            var journal = Read(id) ?? throw new IOException("Unknown archive acknowledgement.");
            if (running.ContainsKey(id) || !journal.Job.Stopped || journal.Job.Response is null || lastSequence != journal.Job.LastSequence)
                throw new IOException("Archive acknowledgement does not cover a completed job.");
            if (!journal.Job.OutputArchived)
                Write(journal with { Job = journal.Job with { OutputArchived = true, AcknowledgedSequence = lastSequence,
                    Response = journal.Job.Response with { Output = null } } });
            var spool = SpoolPath(id); PrivateStorage.RejectLinks(spool); File.Delete(spool);
            var legacy = Path.Combine(root, id.ToString("N")); PrivateStorage.RejectLinks(legacy);
            if (Directory.Exists(legacy))
            {
                var files = Directory.GetFileSystemEntries(legacy);
                foreach (var file in files)
                {
                    PrivateStorage.RejectLinks(file);
                    var name = Path.GetFileName(file);
                    if (name.Length != 25 || !name.EndsWith(".json", StringComparison.Ordinal) || !long.TryParse(name[..20], out var seq) ||
                        seq < 1 || seq > lastSequence || !File.Exists(file)) throw new IOException("Unexpected legacy output entry; preserved.");
                }
                foreach (var file in files) File.Delete(file);
                Directory.Delete(legacy);
            }
            AgentWorkExecutor.FlushDirectory(root);
            spools.Remove(id); journals.Remove(id); outputFailures.Remove(id); health?.Durable(id); health?.Resolved(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Failed(id, "archive-cleanup", error); throw; }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentJobInspection>> PendingArchivesAsync()
    {
        await gate.WaitAsync();
        try
        {
            var ids = Directory.EnumerateFiles(root, "*.output").Select(Path.GetFileNameWithoutExtension)
                .Concat(Directory.EnumerateDirectories(root).Select(Path.GetFileName))
                .Where(x => Guid.TryParseExact(x, "N", out _)).Select(x => Guid.ParseExact(x!, "N")).Distinct().ToArray();
            var result = new List<AgentJobInspection>();
            if (ids.Length == 0) return result;
            for (var i = 0; i < Math.Min(ids.Length, 64); i++)
            {
                var id = ids[(archiveCursor + i) % ids.Length];
                if (!running.ContainsKey(id) && Read(id)?.Job is { Stopped: true, Response: not null } job)
                    result.Add(job with { Response = job.Response with { Output = null } });
            }
            archiveCursor = (archiveCursor + Math.Min(ids.Length, 64)) % ids.Length;
            return result;
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
        persistenceStopping.Cancel();
        try { await CancelAllAsync("shutdown"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Task[] tasks; await gate.WaitAsync();
        try { tasks = running.Values.Select(x => x.Work).ToArray(); } finally { gate.Release(); }
        await Task.WhenAll(tasks);
        if (executor is IReportingAgentWorkExecutor reporting)
        { reporting.Progress -= ProgressAsync; reporting.Output -= OutputAsync; }
        shutdown.Dispose();
        persistenceStopping.Dispose();
    }
}
