using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Stanza.Agent.Runtime;

public sealed record AgentDiskSpace(long AvailableBytes, long TotalBytes);
public interface IAgentDiskSpace
{
    AgentDiskSpace Read(string path);
}
public sealed class AgentDiskSpaceReader : IAgentDiskSpace
{
    public AgentDiskSpace Read(string path)
    {
        var drive = new DriveInfo(Path.GetFullPath(path));
        return new(drive.AvailableFreeSpace, drive.TotalSize);
    }
}

public sealed record AgentStorageStatus(bool Healthy, long? AvailableBytes, long? TotalBytes, long MinimumFreeBytes,
    int WriteFailures, int PendingTerminalResults, int UnresolvedJobs, string? LastErrorType, int? LastErrorCode);

public sealed class AgentStorageHealth(IOptions<AgentOptions> options, IAgentDiskSpace? disk = null)
{
    private readonly ConcurrentDictionary<(Guid Job, string Stage), byte> failures = new();
    private readonly ConcurrentDictionary<Guid, byte> pending = new();
    private readonly ConcurrentDictionary<Guid, byte> unresolved = new();
    private readonly IAgentDiskSpace disk = disk ?? new AgentDiskSpaceReader();
    private long minimumFreeBytes = 2048L * 1024 * 1024;
    private Exception? lastError;

    public void Configure(int minimumFreeMiB)
    {
        if (minimumFreeMiB < 0) throw new ArgumentOutOfRangeException(nameof(minimumFreeMiB));
        Interlocked.Exchange(ref minimumFreeBytes, minimumFreeMiB * 1024L * 1024);
    }
    public bool Failed(Guid id, string stage, Exception error)
    {
        Volatile.Write(ref lastError, error);
        return failures.TryAdd((id, stage), 0);
    }
    public void Succeeded(Guid id, string stage) => failures.TryRemove((id, stage), out _);
    public void Pending(Guid id) => pending.TryAdd(id, 0);
    public void Unresolved(Guid id) => unresolved.TryAdd(id, 0);
    public void Resolved(Guid id) => unresolved.TryRemove(id, out _);
    public void Durable(Guid id)
    {
        pending.TryRemove(id, out _);
        foreach (var key in failures.Keys.Where(x => x.Job == id)) failures.TryRemove(key, out _);
    }
    public AgentStorageStatus Read()
    {
        AgentDiskSpace? space = null;
        try { space = disk.Read(options.Value.PersistentDataRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Volatile.Write(ref lastError, error); }
        var minimum = Interlocked.Read(ref minimumFreeBytes);
        var errorInfo = Volatile.Read(ref lastError);
        return new(space is not null && space.AvailableBytes >= minimum && failures.IsEmpty && pending.IsEmpty && unresolved.IsEmpty,
            space?.AvailableBytes, space?.TotalBytes, minimum, failures.Count, pending.Count, unresolved.Count,
            errorInfo?.GetType().Name, errorInfo?.HResult);
    }
}
