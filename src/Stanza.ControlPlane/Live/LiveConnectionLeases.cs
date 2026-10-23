namespace Stanza.ControlPlane.Live;

// A bounded registry: empty account entries are removed immediately, including on cancellation.
public sealed class LiveConnectionLeases
{
    private readonly int perAccountLimit;
    private readonly int totalLimit;
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> accounts = new();
    private int total;

    public LiveConnectionLeases(int perAccountLimit = 4, int totalLimit = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perAccountLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(totalLimit);
        this.perAccountLimit = perAccountLimit;
        this.totalLimit = totalLimit;
    }

    public IDisposable? TryAcquire(Guid accountId, int? perUser = null, int? maximumTotal = null)
    {
        lock (gate)
        {
            accounts.TryGetValue(accountId, out var count);
            var instance = maximumTotal ?? totalLimit;
            var user = perUser ?? perAccountLimit;
            if (instance > 0 && total >= instance || user > 0 && count >= user) return null;
            accounts[accountId] = count + 1;
            total++;
            return new Lease(this, accountId);
        }
    }

    private void Release(Guid accountId)
    {
        lock (gate)
        {
            var count = accounts[accountId];
            if (count == 1) accounts.Remove(accountId);
            else accounts[accountId] = count - 1;
            total--;
        }
    }

    private sealed class Lease(LiveConnectionLeases owner, Guid accountId) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(accountId);
        }
    }
}
