using System.Text.Json;

namespace Stanza.ControlPlane.Portability;

public sealed class PortabilityException(string message) : Exception(message);
public sealed class PortabilityAccessException() : Exception("You do not have access to every selected portability resource.");

public sealed record PortableFile(string Path, string Kind, string? Text, string? Base64)
{
    public override string ToString() => $"Portable file {Path} ({Kind}, contents redacted)";
}
public sealed record PortableDeployment(string Id, string Name, string? Agent, string Compose, string Environment,
    IReadOnlyList<PortableFile> Files)
{
    public IReadOnlyList<string> SourceDestinations { get; init; } = [];
    public override string ToString() => $"Portable deployment {Id} (contents redacted)";
}
public sealed record PortableAgent(string Id, string Label);
public sealed record PortableResourceReference(string Id, string Type, string Name);
public sealed record PortableResourceCandidate(Guid Id, string Type, string Name);
public sealed record PortableSection(string Kind, JsonElement Content)
{
    public override string ToString() => $"Portable section {Kind} (contents redacted)";
}
public sealed record PortableBundle(int Version, IReadOnlyList<PortableDeployment> Deployments,
    IReadOnlyList<PortableAgent> Agents, IReadOnlyList<PortableSection> Sections)
{
    public IReadOnlyList<PortableResourceReference> References { get; init; } = [];
    public override string ToString() => $"Portable bundle v{Version} (contents redacted)";
}
public sealed record PortableExportSelection(IReadOnlyList<Guid> Deployments, IReadOnlyList<string> Sections,
    bool PlaintextAcknowledged);
public sealed record PortableImportMapping(IReadOnlyDictionary<string, Guid> Agents,
    IReadOnlyDictionary<string, string> DeploymentNames, IReadOnlyDictionary<string, string> Resources);
public sealed record PortablePreview(Guid Id, string Fingerprint, long ExpiresAt, IReadOnlyList<string> Deployments,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Conflicts, bool CanApply);
public sealed record PortableApplyResult(Guid Id, string State, IReadOnlyList<Guid> CreatedDeployments, string? Failure)
{
    public IReadOnlyDictionary<string, Guid> CreatedResources { get; init; } = new Dictionary<string, Guid>();
    public IReadOnlyList<string> CompletedSections { get; init; } = [];
    public string? CurrentSection { get; init; }
}
public sealed record PortableParticipantContext(Guid ActorId, IReadOnlyList<Guid> DeploymentIds,
    IReadOnlyDictionary<Guid, string>? DeploymentReferences = null,
    IReadOnlyDictionary<Guid, string>? AgentReferences = null,
    IReadOnlyDictionary<Guid, PortableResourceReference>? ResourceReferences = null);
public sealed record PortableParticipantImportContext(Guid ActorId, PortableImportMapping Mapping,
    IReadOnlyDictionary<string, Guid> CreatedDeployments, bool ActivateAutomation = false,
    PortableBundle? Bundle = null, IDictionary<string, Guid>? CreatedResources = null,
    IReadOnlyList<PortableResourceReference>? Definitions = null);

/// <summary>Participants must authorize every selected secret/resource; import must create fresh credential IDs,
/// validate all logical references, and keep automation disabled. Never import accounts or runtime identities.</summary>
public interface IPortableConfigurationParticipant
{
    string Kind { get; }
    int Priority => 100;
    bool SecurityPolicy => false;
    Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PortableResourceCandidate>>([]);
    IReadOnlyList<PortableResourceReference> DefinedResources(JsonElement content) => [];
    Task<JsonElement> ExportAsync(PortableParticipantContext context, CancellationToken ct);
    Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext context, CancellationToken ct);
    Task ApplyAsync(JsonElement content, PortableParticipantImportContext context, CancellationToken ct);
}

/// <summary>Integration boundary for creating a complete encrypted revision without implicit grants or activation.</summary>
public interface IPortableDraftStore
{
    Task<PortableDeployment> ReadAsync(Guid actorId, Guid deploymentId, string logicalId, string? logicalAgent, CancellationToken ct);
    Task<Guid> CreateAsync(Guid actorId, PortableDeployment deployment, string name, Guid? agentId, CancellationToken ct);
    Task ReassignAsync(Guid actorId, Guid deploymentId, long expectedRevision, Guid agentId, CancellationToken ct);
}
