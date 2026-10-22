using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Logs;

public sealed record LogTarget(Guid DeploymentId, string Name, Guid AgentId, long Revision, string ObservedState);

public sealed class LogAccessException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class LogAccess(StanzaDbContext db, AccessService access, AccountService accounts, TimeProvider clock)
{
    public async Task<LogTarget> RequireAsync(Guid actor, Guid deploymentId, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, "deployment.read", "deployment", deploymentId, ct)
            || !await access.IsAllowedAsync(actor, "logs.read", "deployment", deploymentId, ct))
            throw new LogAccessException(403, "Deployment read and logs read permissions are required.");
        var deployment = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, ct)
            ?? throw new LogAccessException(404, "Deployment not found.");
        var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, ct);
        // Unlike interactive terminals, history/follow is useful for stopped containers as well.
        // A recorded deployed revision is still required: never forward an unassigned draft.
        if (runtime is null || runtime.Revision <= 0 || deployment.TargetAgentId != runtime.AgentId)
            throw new LogAccessException(409, "Deploy this deployment on its currently assigned Agent before opening container logs.");
        if (!await access.IsAllowedAsync(actor, "agent.read", "agent", runtime.AgentId, ct))
            throw new LogAccessException(403, "Read permission for the assigned Agent is required.");
        if (!await db.Set<AgentEntity>().AsNoTracking().AnyAsync(x => x.Id == runtime.AgentId && x.Status == "approved", ct))
            throw new LogAccessException(409, "The assigned Agent is not approved.");
        return new(deployment.Id, deployment.Name, runtime.AgentId, runtime.Revision, runtime.ObservedState);
    }

    public async Task<bool> IsCurrentAsync(Guid actor, string stamp, DateTimeOffset expires, LogTarget target, CancellationToken ct)
    {
        var account = await accounts.FindAsync(actor, ct);
        if (clock.GetUtcNow() >= expires || account is null || !account.Enabled || account.SecurityStamp != stamp) return false;
        try
        {
            var current = await RequireAsync(actor, target.DeploymentId, ct);
            return current.AgentId == target.AgentId && current.Revision == target.Revision;
        }
        catch (LogAccessException) { return false; }
    }
}
