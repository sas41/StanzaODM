using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Portability;

public sealed class PortablePartialCreationException(Guid deploymentId) : Exception("Configuration creation was interrupted; the reported deployment contains partial configuration and remains inactive.")
{
    public Guid DeploymentId { get; } = deploymentId;
}

/// <summary>Creates complete initial workspaces in one authorized encrypted revision.</summary>
public sealed class PortableDraftStore(DeploymentDraftService drafts, AccessService access) : IPortableDraftStore
{
    public async Task<PortableDeployment> ReadAsync(Guid actorId, Guid deploymentId, string logicalId, string? logicalAgent, CancellationToken ct)
    {
        var draft = await drafts.ReadAsync(actorId, deploymentId, ct: ct);
        if (draft.ComposeText is null || draft.EnvironmentText is null) throw new PortabilityAccessException();
        var files = new List<PortableFile>();
        foreach (var metadata in draft.Files)
        {
            var file = await drafts.ReadFileAsync(actorId, deploymentId, metadata.Path, draft.ViewedRevision, ct);
            files.Add(file.Kind switch
            {
                ManagedEntryKind.Text => new(metadata.Path, "text", file.GetText(), null),
                ManagedEntryKind.Directory => new(metadata.Path, "directory", null, null),
                _ => new(metadata.Path, "binary", null, Convert.ToBase64String(file.GetBytes()))
            });
        }
        return new(logicalId, draft.Draft.Name, logicalAgent, draft.ComposeText, draft.EnvironmentText, files);
    }

    public async Task<Guid> CreateAsync(Guid actorId, PortableDeployment deployment, string name, Guid? agentId, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actorId, "configuration.import", "instance", null, ct)) throw new PortabilityAccessException();
        var files = deployment.Files.Select(file => file.Kind switch
        {
            "text" => ManagedFileSnapshot.Text(file.Path, file.Text!),
            "directory" => ManagedFileSnapshot.Directory(file.Path),
            "binary" => ManagedFileSnapshot.Binary(file.Path, Convert.FromBase64String(file.Base64!)),
            _ => throw new PortabilityException("Unsupported managed file kind.")
        }).ToArray();
        var sourcePaths = deployment.SourceDestinations.Select(path => new GitSourceWorkspace(Guid.NewGuid(), path)).ToArray();
        var created = await drafts.CreatePortableWorkspaceAsync(actorId, name, agentId, deployment.Compose, deployment.Environment, files, sourcePaths, ct);
        return created.Id;
    }

    public async Task ReassignAsync(Guid actorId, Guid deploymentId, long expectedRevision, Guid agentId, CancellationToken ct)
    {
        await drafts.ReassignPortablePlacementAsync(actorId, deploymentId, expectedRevision, agentId, ct);
    }
}
