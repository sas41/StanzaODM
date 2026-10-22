using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Automation;

/// <summary>Call immediately before dispatching any operation (and during sensitive execution).
/// Non-automation triggers are ignored. This closes the window between automation enqueue and
/// operation dispatch when an action grant, schedule, webhook, or owner is revoked.</summary>
public sealed class AutomationOperationGuard(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization)
{
    public async Task AuthorizeAsync(Guid actorId, string trigger, string kind, Guid? deploymentId, Guid agentId, CancellationToken ct = default)
    {
        if (!trigger.StartsWith("automation:", StringComparison.Ordinal)) return;
        if (!Guid.TryParseExact(trigger[11..], "N", out var id)) throw new AutomationAccessException();
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null || run.OwnerId != actorId || run.State != "running" || run.CancelRequested || run.LeaseUntil < automation.Now)
            throw new AutomationAccessException();
        if (run.ActionId is { } actionId)
        {
            var definition = await automation.ActionAsync(actionId, ct);
            if (!definition.Enabled) throw new AutomationAccessException();
            var plan = automation.Unprotect<ActionPlan>(run.ProtectedPlan);
            var step = plan.Steps.FirstOrDefault(s => AutomationRules.OperationKind(s.Kind) == kind && s.DeploymentId == deploymentId && (s.Kind != "host-exec" || s.AgentId == agentId));
            if (step is null) throw new AutomationAccessException();
            foreach (var owner in new Guid?[] { run.OwnerId, definition.OwnerId, run.ActionOwnerId }.Where(x => x is not null).Select(x => x!.Value).Distinct())
            {
                await authorization.RequireAsync(owner, "action.execute", "action", actionId, ct);
                await authorization.PlanAsync(owner, plan, ct);
            }
        }
        else
        {
            if (run.DeploymentId != deploymentId || (run.Operation != "image-check" && AutomationRules.OperationKind(run.Operation) != kind)) throw new AutomationAccessException();
            await authorization.TargetAsync(actorId, new(run.Operation == "git-refresh" ? "git-refresh" : kind, deploymentId), ct);
        }
        if (run.Source == "schedule")
        {
            var s = await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (s is null || !s.Enabled || s.OwnerId != actorId) throw new AutomationAccessException();
        }
        if (run.Source == "webhook")
        {
            var h = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (h is null || !h.Enabled || h.ProtectedSecret.Length == 0 || h.OwnerId != actorId) throw new AutomationAccessException();
        }
        if (run.Source is "image-policy" or "manual-image-policy")
        {
            var p = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (p is null || p.OwnerId != actorId || (run.Source == "image-policy" && !p.Enabled) || kind != "deploy" || p.Mode != "auto-deploy")
                throw new AutomationAccessException();
            if (ImagePolicySnapshot.From(p) != automation.Unprotect<ImagePolicySnapshot>(run.ProtectedPlan)) throw new AutomationAccessException();
            await authorization.RequireAsync(run.RequestedBy, "automation.manage", "deployment", p.DeploymentId, ct);
            await authorization.RequireAsync(run.RequestedBy, "deployment.deploy", "deployment", p.DeploymentId, ct);
        }
        if (run.Source is "schedule" or "webhook" or "image-policy" or "manual-image-policy")
            await authorization.RequireAsync(actorId, "automation.manage", "deployment", run.DeploymentId, ct);
    }
}
