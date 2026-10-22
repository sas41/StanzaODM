using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Portability.Participants;

namespace Stanza.ControlPlane.Portability;

public static class PortabilityRegistration
{
    public static IServiceCollection AddStanzaPortability(this IServiceCollection services)
    {
        services.TryAddScoped<IPortableDraftStore, PortableDraftStore>();
        services.TryAddScoped<IPortableMoveHealthVerifier, PortableMoveHealthVerifier>();
        services.AddScoped<PortabilityService>();
        services.AddScoped<PortableDestinationPreviewService>();
        services.AddScoped<PortableMoveService>();
        services.AddScoped<ParticipantSupport>();
        services.AddScoped<EnvironmentParticipant>();
        services.AddScoped<IPortableConfigurationParticipant, SourcesParticipant>();
        services.AddScoped<IPortableConfigurationParticipant>(s => s.GetRequiredService<EnvironmentParticipant>());
        services.AddScoped<IPortableConfigurationParticipant, AgentTemplatesParticipant>();
        services.AddScoped<IPortableConfigurationParticipant, PublicSettingsParticipant>();
        services.AddScoped<IPortableConfigurationParticipant, PoliciesParticipant>();
        services.AddScoped<IPortableConfigurationParticipant, AutomationParticipant>();
        services.AddScoped<IPortableConfigurationParticipant, GroupsParticipant>();
        services.AddHostedService<PortableMoveWorker>();
        return services;
    }
}
