using Stanza.ControlPlane.Live;

namespace Stanza.Tests.Live;

public sealed class LiveConnectionLeasesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void RejectsInvalidBounds(int perAccount, int total) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveConnectionLeases(perAccount, total));

    [Fact]
    public void EnforcesPerAccountLimitWithoutBlockingOtherAccounts()
    {
        var leases = new LiveConnectionLeases(2, 10);
        var account = Guid.NewGuid();
        using var first = leases.TryAcquire(account);
        using var second = leases.TryAcquire(account);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(leases.TryAcquire(account));
        using var other = leases.TryAcquire(Guid.NewGuid());
        Assert.NotNull(other);
        first.Dispose();
        using var replacement = leases.TryAcquire(account);
        Assert.NotNull(replacement);
    }

    [Fact]
    public void TotalLimitBoundsDistinctAccountsAndDisposalIsIdempotent()
    {
        var leases = new LiveConnectionLeases(4, 1);
        var first = leases.TryAcquire(Guid.NewGuid());
        Assert.NotNull(first);
        Assert.Null(leases.TryAcquire(Guid.NewGuid()));
        first.Dispose();
        first.Dispose();
        using var second = leases.TryAcquire(Guid.NewGuid());
        Assert.NotNull(second);
        Assert.Null(leases.TryAcquire(Guid.NewGuid()));
    }

    [Fact]
    public async Task ConcurrentAcquisitionCannotExceedLimitAndReleasesAllSlots()
    {
        var leases = new LiveConnectionLeases(4, 100);
        var account = Guid.NewGuid();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => leases.TryAcquire(account))));
        Assert.Equal(4, attempts.Count(lease => lease is not null));
        await Task.WhenAll(attempts.Where(lease => lease is not null).Select(lease => Task.Run(() => lease!.Dispose())));
        var next = Enumerable.Range(0, 4).Select(_ => leases.TryAcquire(account)).ToArray();
        Assert.All(next, lease => Assert.NotNull(lease));
        foreach (var lease in next) lease!.Dispose();
    }
}
