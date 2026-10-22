using Stanza.Core.Deployments;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Deployments;

internal sealed record DeploymentExecutionSnapshot(Guid Id, string Name, Guid? AgentId, long Revision,
    IReadOnlyList<AgentFile> Files, string EnvironmentText)
{
    public override string ToString() => $"DeploymentExecutionSnapshot {{ Id = {Id}, Revision = {Revision}, Contents = [REDACTED] }}";
}

public sealed class DeploymentDraftException(string message) : Exception(message);
public sealed class DeploymentDraftAccessException() : Exception("You do not have permission to access this deployment resource.");
public sealed class DeploymentDraftConflictException() : Exception("This draft was changed by another editor. Reload the latest revision before saving; your changes were not saved.");

public sealed record DeploymentDraftSummary(Guid Id, string Name, Guid? TargetAgentId, long Revision,
    DateTimeOffset UpdatedAt);
public sealed record DeploymentDraftRevisionSummary(long Number, Guid SavedBy, DateTimeOffset SavedAt);
public sealed record DeploymentDraftFileSummary(string Path, ManagedEntryKind Kind, long Length);
public sealed record DeploymentAgentOption(Guid Id, string Hostname);
public sealed record DeploymentDraftPermissions(bool CanCreate, bool CanReadFiles, bool CanWriteFiles,
    bool CanRevealEnvironment, bool CanWriteEnvironment);

public sealed record DeploymentDraftView(DeploymentDraftSummary Draft, long ViewedRevision,
    string? ComposeText, string? EnvironmentText, IReadOnlyList<DeploymentDraftFileSummary> Files,
    IReadOnlyList<DeploymentDraftRevisionSummary> History, DeploymentDraftPermissions Permissions)
{
    public long? DeployedRevision { get; init; }
    public bool CanValidate { get; init; }
    public override string ToString() => $"Draft {Draft.Id}, revision {ViewedRevision} (contents redacted)";
}

/// <summary>Null content means preserve the existing content, not replace it with an empty file.</summary>
public sealed record DeploymentDraftEdit(string Name, Guid? TargetAgentId, long ExpectedRevision,
    string? ComposeText, string? EnvironmentText)
{
    public override string ToString() => $"Draft edit at revision {ExpectedRevision} (contents redacted)";
}
