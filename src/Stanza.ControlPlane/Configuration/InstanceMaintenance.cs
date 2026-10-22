namespace Stanza.ControlPlane.Configuration;

public sealed class InstanceMaintenance
{
    private int paused;
    private int restartRequired;
    public bool Paused => Volatile.Read(ref paused) != 0;
    public bool RestartRequired => Volatile.Read(ref restartRequired) != 0;
    public IDisposable BeginRestore()
    {
        if (Interlocked.CompareExchange(ref paused, 1, 0) != 0) throw new InvalidOperationException("Recovery is already in progress.");
        return new Lease(this);
    }
    public void RequireRestart() { Volatile.Write(ref restartRequired, 1); Volatile.Write(ref paused, 1); }
    private sealed class Lease(InstanceMaintenance owner) : IDisposable
    {
        public void Dispose() { if (!owner.RestartRequired) Volatile.Write(ref owner.paused, 0); }
    }
}
