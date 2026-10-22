using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Live;

namespace Stanza.Tests.Live;

public sealed class LiveStatusRegistrationTests
{
    [Fact]
    public void RegistrationIsIdempotentAndSuppliesDefaultSingletons()
    {
        var services = new ServiceCollection();
        services.AddStanzaLiveStatus().AddStanzaLiveStatus();
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<LiveConnectionLeases>());
        Assert.Single(provider.GetServices<TimeProvider>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Same(provider.GetRequiredService<LiveConnectionLeases>(), provider.GetRequiredService<LiveConnectionLeases>());
    }

    [Fact]
    public void ExistingClockAndConnectionTrackerArePreserved()
    {
        var clock = new LiveTestClock();
        var tracker = new LiveConnectionLeases(1, 1);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(tracker);
        services.AddStanzaLiveStatus();
        using var provider = services.BuildServiceProvider();
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Same(tracker, provider.GetRequiredService<LiveConnectionLeases>());
    }
}
