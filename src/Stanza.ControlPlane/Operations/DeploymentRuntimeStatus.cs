using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;

namespace Stanza.ControlPlane.Operations;

public sealed record DeploymentRuntimeStatus(Guid AgentId, long Revision, string DesiredState, string ObservedState,
    DateTimeOffset? ObservedAt, bool AgentConnected, bool IsStale)
{
    public string Availability => !AgentConnected ? "Offline — current state unknown" :
        ObservedAt is null ? "Not yet observed" : IsStale ? "Stale observation — inspect current status" : "Recent observation";
}

public sealed class DeploymentRuntimeStatusService(StanzaDbContext db, AccessService access,
    IAgentCommandDispatcher agents, OperationalPolicyService policies, TimeProvider clock)
{
    public async Task<DeploymentRuntimeStatus?> ReadAsync(Guid actor, Guid deployment, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct))
            throw new DeploymentDraftAccessException();
        var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment, ct);
        if (runtime is null) return null;
        var observed = runtime.ObservedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(runtime.ObservedAt) : (DateTimeOffset?)null;
        var interval = (await policies.GetAsync(ct)).Policy.StatusIntervalSeconds;
        return new(runtime.AgentId, runtime.Revision, runtime.DesiredState, runtime.ObservedState, observed,
            agents.IsConnected(runtime.AgentId), observed is null || clock.GetUtcNow() - observed > TimeSpan.FromSeconds(interval));
    }
}
