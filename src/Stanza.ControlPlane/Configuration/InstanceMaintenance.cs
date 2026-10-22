namespace Stanza.ControlPlane.Configuration;

public sealed class InstanceMaintenance
{
    private int paused;
    private int restartRequired;
    private byte[]? restartTicket;
    public bool Paused => Volatile.Read(ref paused) != 0;
    public bool RestartRequired => Volatile.Read(ref restartRequired) != 0;
    public IDisposable BeginRestore()
    {
        if (Interlocked.CompareExchange(ref paused, 1, 0) != 0) throw new InvalidOperationException("Recovery is already in progress.");
        return new Lease(this);
    }
    public void RequireRestart() { Volatile.Write(ref restartRequired, 1); Volatile.Write(ref paused, 1); }
    public string IssueRestartTicket()
    {
        if (!RestartRequired) throw new InvalidOperationException("Recovery is not waiting for a restart.");
        var value = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Interlocked.Exchange(ref restartTicket, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        return value;
    }
    public bool ConsumeRestartTicket(string? value)
    {
        if (!RestartRequired || value is not { Length: 64 }) return false;
        var expected = Volatile.Read(ref restartTicket);
        if (expected is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))) return false;
        return Interlocked.CompareExchange(ref restartTicket, null, expected) == expected;
    }
    private sealed class Lease(InstanceMaintenance owner) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0 && !owner.RestartRequired) Volatile.Write(ref owner.paused, 0); }
    }
}
