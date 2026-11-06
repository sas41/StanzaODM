using Stanza.ControlPlane.Transport;

namespace Stanza.Tests.Transport;

public sealed class AgentDiscoveryAdmissionTests
{
    [Fact]
    public async Task CapacityWaitsLocallyAndSeparateAgentPoolsRemainIndependent()
    {
        var first = new AgentDiscoveryAdmission(); var other = new AgentDiscoveryAdmission();
        var occupied = await first.EnterAsync(1, default);
        var waiting = first.EnterAsync(1, default); Assert.False(waiting.IsCompleted);
        using var independent = await other.EnterAsync(1, default).WaitAsync(TimeSpan.FromSeconds(1));
        occupied.Dispose(); occupied.Dispose(); // Double disposal cannot create an extra slot.
        using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(); var stillFull = first.EnterAsync(1, cancellation.Token);
        Assert.False(stillFull.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stillFull);
    }

    [Fact]
    public async Task LowerCapacityWaitsForExistingAdmissionsWithoutCancellingThem()
    {
        var pool = new AgentDiscoveryAdmission(); var a = await pool.EnterAsync(2, default); var b = await pool.EnterAsync(2, default);
        var reduced = pool.EnterAsync(1, default); Assert.False(reduced.IsCompleted);
        a.Dispose(); Assert.False(reduced.IsCompleted);
        b.Dispose(); using var next = await reduced.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)][InlineData(65)]
    public async Task InvalidCapacityIsRejected(int capacity) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AgentDiscoveryAdmission().EnterAsync(capacity, default));
}
