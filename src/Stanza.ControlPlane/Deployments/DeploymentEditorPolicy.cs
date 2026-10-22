namespace Stanza.ControlPlane.Deployments;

/// <summary>Application settings adapter. The provider must return a current, validated policy.</summary>
public interface IDeploymentEditorLimitsProvider
{
    Task<DeploymentEditorLimits> GetLimitsAsync(CancellationToken ct = default);
}

public sealed record DeploymentEditorLimits(int TextBytes, int UploadBytes, int WorkspaceBytes, int SupportingEntries)
{
    public static DeploymentEditorLimits Default { get; } = new(DeploymentDraftService.MaximumTextBytes,
        DeploymentDraftService.MaximumUploadBytes, DeploymentDraftService.MaximumWorkspaceBytes,
        DeploymentDraftService.MaximumSupportingFiles);
}

/// <summary>Only successfully applied revisions should be returned; null means never deployed/unknown.</summary>
public interface IDeploymentRevisionStatusProvider
{
    Task<long?> GetDeployedRevisionAsync(Guid deploymentId, CancellationToken ct = default);
}

/// <summary>Source subsystem adapter: reserved checkout destinations must participate in managed-file collision checks.</summary>
public interface IDeploymentWorkspaceReservations
{
    Task<IReadOnlyList<Stanza.Core.Deployments.GitSourceWorkspace>> GetSourcesAsync(Guid deploymentId, CancellationToken ct = default);
}
