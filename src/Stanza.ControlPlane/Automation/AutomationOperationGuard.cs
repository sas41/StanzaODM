using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Automation;

/// <summary>Call immediately before dispatching any operation (and during sensitive execution).
/// Non-automation triggers are ignored. This closes the window between automation enqueue and
/// operation dispatch when an action grant, schedule, webhook, or owner is revoked.</summary>
public sealed class AutomationOperationGuard(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization)
{
    internal async Task<ActionExecutionBindings?> AuthoredBindingsAsync(string trigger, Guid? deployment, string kind, CancellationToken ct)
    {
        if (!trigger.StartsWith("automation:", StringComparison.Ordinal) || !Guid.TryParseExact(trigger[11..], "N", out var id)) return null;
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run?.ActionId is null) return null;
        var plan = automation.Unprotect<ActionPlan>(run.ProtectedPlan);
        var values = automation.Unprotect<Dictionary<string, string>>(run.ProtectedParameters);
        var bindings = AutomationRules.Bindings(plan, deployment, kind, values);
        return bindings.Sources.Count == 0 && bindings.Images.Count == 0 ? null : new(bindings.Sources, bindings.Images);
    }

    public async Task ValidateSubmissionAsync(Operations.OperationSubmission submission, CancellationToken ct)
    {
        if (!submission.Trigger.StartsWith("automation:", StringComparison.Ordinal))
        {
            if (submission.SourceOverrides?.Count > 0 || submission.ImageOverrides?.Count > 0) throw new AutomationAccessException();
            return;
        }
        if (!Guid.TryParseExact(submission.Trigger[11..], "N", out var id)) throw new AutomationAccessException();
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationAccessException();
        await AuthorizeAsync(submission.ActorId, submission.Trigger, submission.Kind, submission.DeploymentId, submission.AgentId ?? Guid.Empty, ct);
        if (run.ActionId is null) return;
        var plan = automation.Unprotect<ActionPlan>(run.ProtectedPlan);
        var values = automation.Unprotect<Dictionary<string, string>>(run.ProtectedParameters);
        var candidates = plan.Steps.Where(s => AutomationRules.OperationKind(s.Kind) == submission.Kind && s.DeploymentId == submission.DeploymentId && s.AgentId == submission.AgentId);
        foreach (var step in candidates)
        {
            var resolved = AutomationRules.Resolve(step, values);
            var bindings = automation.ExecutionBindings(run, plan, step, values);
            if (System.Text.Json.JsonSerializer.Serialize(resolved.Execution) == System.Text.Json.JsonSerializer.Serialize(submission.Execution) &&
                Same(bindings.Sources, submission.SourceOverrides) && Same(bindings.Images, submission.ImageOverrides)) return;
        }
        throw new AutomationAccessException();
    }

    private static bool Same(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string>? actual) =>
        expected.Count == (actual?.Count ?? 0) && expected.All(p => actual is not null && actual.TryGetValue(p.Key, out var value) && p.Value == value);

    public async Task AuthorizeAsync(Guid actorId, string trigger, string kind, Guid? deploymentId, Guid agentId, CancellationToken ct = default)
    {
        if (!trigger.StartsWith("automation:", StringComparison.Ordinal)) return;
        if (!Guid.TryParseExact(trigger[11..], "N", out var id)) throw new AutomationAccessException();
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null || (run.ActionId.HasValue ? run.ActionOwnerId : run.OwnerId) != actorId || run.State != "running" || run.CancelRequested || run.LeaseUntil < automation.Now)
            throw new AutomationAccessException();
        if (run.ActionId is { } actionId)
        {
            var definition = await automation.ActionAsync(actionId, ct);
            if (!definition.Enabled || definition.OwnerId != run.ActionOwnerId) throw new AutomationAccessException();
            var plan = automation.Unprotect<ActionPlan>(run.ProtectedPlan);
            var step = plan.Steps.FirstOrDefault(s => AutomationRules.OperationKind(s.Kind) == kind && s.DeploymentId == deploymentId && (s.Kind != "host-exec" || s.AgentId == agentId));
            if (step is null) throw new AutomationAccessException();
            await authorization.RequireAsync(run.OwnerId, "action.execute", "action", actionId, ct);
            await authorization.RequireAsync(run.RequestedBy, "action.execute", "action", actionId, ct);
            await authorization.RequireAsync(actorId, "action.execute", "action", actionId, ct);
            await authorization.PlanAsync(actorId, plan, ct);
        }
        else
        {
            if (run.DeploymentId != deploymentId || (run.Operation != "image-check" && AutomationRules.OperationKind(run.Operation) != kind)) throw new AutomationAccessException();
            await authorization.TargetAsync(actorId, new(run.Operation == "git-refresh" ? "git-refresh" : kind, deploymentId), ct);
        }
        if (run.Source == "schedule")
        {
            var s = await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (s is null || !s.Enabled || s.OwnerId != run.OwnerId || s.ActionId != run.ActionId || s.DeploymentId != run.DeploymentId)
                throw new AutomationAccessException();
            await authorization.ScheduleAsync(run.OwnerId, s.ActionId, s.DeploymentId, ct);
        }
        if (run.Source == "webhook")
        {
            var h = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.DefinitionId, ct);
            if (h is null || !h.Enabled || h.ProtectedSecret.Length == 0 || h.OwnerId != run.OwnerId) throw new AutomationAccessException();
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
        if (run.Source is "webhook" or "image-policy" or "manual-image-policy")
            await authorization.RequireAsync(run.OwnerId, "automation.manage", "deployment", run.DeploymentId, ct);
    }
}
