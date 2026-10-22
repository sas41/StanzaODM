using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Terminals;

public sealed record TerminalTarget(Guid DeploymentId, string Name, Guid AgentId, long Revision);
public sealed class TerminalAccessException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class TerminalAccess(StanzaDbContext db, AccessService access, AccountService accounts, TimeProvider clock, AgentPolicyService? policies = null)
{
    public async Task<TerminalTarget> RequireAsync(Guid actor, Guid deploymentId, CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(actor, "deployment.read", "deployment", deploymentId, ct)
            || !await access.IsAllowedAsync(actor, "terminal.open", "deployment", deploymentId, ct))
            throw new TerminalAccessException(403, "Deployment read and terminal open permissions are required.");
        var deployment = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, ct)
            ?? throw new TerminalAccessException(404, "Deployment not found.");
        var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, ct);
        if (runtime is null || runtime.Revision <= 0 || runtime.ObservedState != "running"
            || deployment.TargetAgentId != runtime.AgentId)
            throw new TerminalAccessException(409, "The deployment must be running on its currently assigned Agent before opening a terminal.");
        if (!await access.IsAllowedAsync(actor, "agent.read", "agent", runtime.AgentId, ct))
            throw new TerminalAccessException(403, "Read permission for the assigned Agent is required.");
        if (!await db.Set<AgentEntity>().AsNoTracking().AnyAsync(x => x.Id == runtime.AgentId && x.Status == "approved", ct))
            throw new TerminalAccessException(409, "The assigned Agent is not approved.");
        if (policies is not null && !(await policies.RuntimeAsync(runtime.AgentId, ct)).ExecutionEnabled) throw new TerminalAccessException(409, "Agent execution has been disabled.");
        return new(deployment.Id, deployment.Name, runtime.AgentId, runtime.Revision);
    }

    public async Task<bool> IsCurrentAsync(Guid actor, string stamp, DateTimeOffset expires, TerminalTarget target, CancellationToken ct)
    {
        var account = await accounts.FindAsync(actor, ct);
        if (clock.GetUtcNow() >= expires || account is null || !account.Enabled || account.SecurityStamp != stamp) return false;
        try
        {
            var current = await RequireAsync(actor, target.DeploymentId, ct);
            return current.AgentId == target.AgentId && current.Revision == target.Revision;
        }
        catch (TerminalAccessException) { return false; }
    }
}
