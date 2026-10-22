using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Terminals;

namespace Stanza.Tests.Terminals;

public sealed class TerminalRegistrationTests
{
    [Fact]
    public void RegistrationIsIdempotentPreservesClockAndRequiresAnExplicitRealTransport()
    {
        var services = new ServiceCollection();
        var clock = new Stanza.Tests.Live.LiveTestClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddStanzaTerminals().AddStanzaTerminals();
        Assert.Same(clock, Assert.Single(services, service => service.ServiceType == typeof(TimeProvider)).ImplementationInstance);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, service => service.ServiceType == typeof(LiveConnectionLeases)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, service => service.ServiceType == typeof(TerminalAccess)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, service => service.ServiceType == typeof(AuditService)).Lifetime);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IAgentTerminalTransport));
    }
}
