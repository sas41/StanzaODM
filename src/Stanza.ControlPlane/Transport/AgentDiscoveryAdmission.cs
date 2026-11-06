namespace Stanza.ControlPlane.Transport;

/// <summary>Backpressure for one Agent's discovery lane, never an instance-wide execution cap.</summary>
public sealed class AgentDiscoveryAdmission
{
    private readonly object gate = new();
    private int active;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IDisposable> EnterAsync(int capacity, CancellationToken ct)
    {
        if (capacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (gate)
            {
                if (active < capacity) { active++; return new Lease(this); }
                wait = changed.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    private void Release()
    {
        lock (gate)
        {
            active--;
            var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private sealed class Lease(AgentDiscoveryAdmission owner) : IDisposable
    {
        private AgentDiscoveryAdmission? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
