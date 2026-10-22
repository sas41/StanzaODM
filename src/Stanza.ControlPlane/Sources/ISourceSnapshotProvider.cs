using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Sources;

public sealed record DeploymentSourcesSnapshot(IReadOnlyList<AgentGitSource> Git, IReadOnlyList<AgentRegistryCredential> Registries)
{
    public override string ToString() => "DeploymentSourcesSnapshot { [REDACTED] }";
}
public interface ISourceSnapshotProvider
{
    Task<DeploymentSourcesSnapshot> PrepareAsync(Guid actorId, Guid deploymentId, CancellationToken token = default);
}
