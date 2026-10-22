using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Portability;

namespace Stanza.ControlPlane.Models;

public sealed record PortabilityAgentModel(Guid Id, string Hostname);
public sealed record PortabilityIndexModel(IReadOnlyList<DeploymentDraftSummary> Deployments,
    IReadOnlyList<PortabilityAgentModel> Agents, IReadOnlyList<string> SupportedSections);
