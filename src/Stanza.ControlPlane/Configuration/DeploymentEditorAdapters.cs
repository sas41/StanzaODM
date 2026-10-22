using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Configuration;

public sealed class DeploymentEditorAdapters(StanzaDbContext db, OperationalPolicyService policies) : IDeploymentEditorLimitsProvider, IDeploymentWorkspaceReservations, IDeploymentRevisionStatusProvider
{
    public async Task<DeploymentEditorLimits> GetLimitsAsync(CancellationToken ct = default)
    {
        var p = (await policies.GetAsync(ct)).Policy;
        return new(p.MaximumTextBytes, p.MaximumUploadBytes, p.MaximumWorkspaceBytes, p.MaximumSupportingFiles);
    }
    public async Task<IReadOnlyList<GitSourceWorkspace>> GetSourcesAsync(Guid deploymentId, CancellationToken ct = default)
    {
        var sources = await db.Set<DeploymentGitSourceEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId).Select(x => new { x.Id, x.Destination }).ToListAsync(ct);
        return sources.Select(x => new GitSourceWorkspace(x.Id, x.Destination)).ToArray();
    }
    public async Task<long?> GetDeployedRevisionAsync(Guid deploymentId, CancellationToken ct = default) =>
        await db.Set<DeploymentRuntimeEntity>().AsNoTracking().Where(x => x.DeploymentId == deploymentId && x.Revision > 0).Select(x => (long?)x.Revision).SingleOrDefaultAsync(ct);
}
