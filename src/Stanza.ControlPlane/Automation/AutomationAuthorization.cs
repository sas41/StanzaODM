using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Automation;

public sealed class AutomationAuthorization(AccessService access)
{
    public async Task RequireAsync(Guid actor, string permission, string type, Guid? id, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actor, permission, type, id, ct)) throw new AutomationAccessException();
    }

    public Task TargetAsync(Guid actor, ActionStep step, CancellationToken ct = default) => step.Kind switch
    {
        "git-refresh" => GitRefreshAsync(actor, step.DeploymentId, ct),
        "host-exec" => RequireAsync(actor, "host.execute", "agent", step.AgentId, ct),
        "container-exec" => RequireAsync(actor, "container.execute", "deployment", step.DeploymentId, ct),
        "pull" => RequireAsync(actor, "deployment.deploy", "deployment", step.DeploymentId, ct),
        "status" => RequireAsync(actor, "deployment.read", "deployment", step.DeploymentId, ct),
        "logs" => RequireAsync(actor, "logs.read", "deployment", step.DeploymentId, ct),
        _ => RequireAsync(actor, "deployment." + step.Kind, "deployment", step.DeploymentId, ct)
    };

    private async Task GitRefreshAsync(Guid actor, Guid? deployment, CancellationToken ct)
    {
        await RequireAsync(actor, "sources.manage", "deployment", deployment, ct);
        await RequireAsync(actor, "deployment.validate", "deployment", deployment, ct);
    }

    public async Task PlanAsync(Guid actor, ActionPlan plan, CancellationToken ct = default)
    {
        foreach (var step in plan.Steps) await TargetAsync(actor, step, ct);
    }
}
