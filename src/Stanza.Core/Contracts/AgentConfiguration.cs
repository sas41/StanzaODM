namespace Stanza.Core.Contracts;

// Configuration replication is not a workload operation. Credentials remain operation-scoped.
public sealed record AgentConfiguration(Guid AgentId, string GlobalEnvironment, string AgentEnvironment,
    AgentRuntimePolicy Policy, IReadOnlyList<Guid> DeploymentIds)
{
    public override string ToString() => $"AgentConfiguration {{ AgentId = {AgentId}, Contents = [REDACTED] }}";
}

public sealed record AgentSourceDefinition(string Url, string Reference, string Destination, Guid? CredentialId);

public sealed record AgentDeploymentConfiguration(Guid Id, string Name, long Revision,
    IReadOnlyList<AgentFile> Files, string Environment, IReadOnlyList<AgentSourceDefinition> Sources,
    IReadOnlyList<Guid> RegistryCredentialIds)
{
    public override string ToString() => $"AgentDeploymentConfiguration {{ Id = {Id}, Revision = {Revision}, Contents = [REDACTED] }}";
}
