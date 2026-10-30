using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Operations;

public sealed record OperationsRow(Guid Id, bool IsActionRun, string Kind, string ActionName, string Agent, string Deployment,
    string Trigger, string State, string? Code, long CreatedAt, long? StartedAt, long? CompletedAt)
{
    public Guid? ActionRunId { get; init; }
    public string Url => (IsActionRun ? "/automation/runs/" : "/operations/") + Id;
    public string Duration(long now) => StartedAt is { } started ? TimeSpan.FromSeconds(Math.Max(0, (CompletedAt ?? now) - started)).ToString("c") : "—";
}

public sealed class OperationsOverviewService(StanzaDbContext db, OperationService operations, AutomationService automation, AccessService access)
{
    public async Task<IReadOnlyList<OperationsRow>> ListAsync(Guid actor, Guid? deployment, CancellationToken ct = default)
    {
        var result = new List<OperationsRow>();
        var operationRows = await operations.ListAsync(actor, deployment, ct);
        var correlatedIds = operationRows.Where(x => x.Trigger.StartsWith("automation:", StringComparison.Ordinal))
            .Select(x => Guid.TryParseExact(x.Trigger[11..], "N", out var id) ? id : Guid.Empty).Distinct().ToArray();
        var users = await db.Accounts.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Username, ct);
        var runs = await db.Set<AutomationRun>().AsNoTracking().Where(x => correlatedIds.Contains(x.Id) ||
            (x.State == "queued" || x.State == "running") && (!deployment.HasValue || x.DeploymentId == deployment)).Select(x => new AutomationRun {
                Id = x.Id, ActionId = x.ActionId, DeploymentId = x.DeploymentId, OwnerId = x.OwnerId, RequestedBy = x.RequestedBy,
                Source = x.Source, State = x.State, Code = x.Code, CreatedAt = x.CreatedAt, StartedAt = x.StartedAt, CompletedAt = x.CompletedAt,
                ProtectedPlan = x.DeploymentId == null && (x.State == "queued" || x.State == "running") ? x.ProtectedPlan : ""
            }).ToDictionaryAsync(x => x.Id, ct);
        var actions = await db.Set<ActionDefinition>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var agents = await db.Set<AgentEntity>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Hostname, ct);
        var deployments = await db.Set<DeploymentDraftEntity>().AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        async Task<string> AgentName(Guid? id) => id is { } value && await access.IsAllowedAsync(actor, "agent.read", "agent", value, ct)
            ? agents.GetValueOrDefault(value, value.ToString()) : "—";
        async Task<string> DeploymentName(Guid? id) => id is { } value && await access.IsAllowedAsync(actor, "deployment.read", "deployment", value, ct)
            ? deployments.GetValueOrDefault(value)?.Name ?? value.ToString() : "—";
        async Task<string> ActionName(AutomationRun? run) => run?.ActionId is { } id && await automation.CanReadRun(actor, run, ct)
            ? actions.GetValueOrDefault(id) ?? "Deleted action" : "—";
        string Trigger(AutomationRun? run, Guid user, string fallback) => run is null
            ? (fallback == "manual" ? users.GetValueOrDefault(user, "Deleted user") : fallback)
            : run.Source switch {
                "schedule" => "Cron", "webhook" => "Webhook", "image-policy" => "Image policy",
                _ => users.GetValueOrDefault(run.RequestedBy, "Deleted user") + (run.Source == "manual" ? "" : " · " + run.Source)
            };
        foreach (var op in operationRows)
        {
            AutomationRun? run = null;
            if (op.Trigger.StartsWith("automation:", StringComparison.Ordinal) && Guid.TryParseExact(op.Trigger[11..], "N", out var runId)) run = runs.GetValueOrDefault(runId);
            result.Add(new(op.Id, false, op.Kind, await ActionName(run), await AgentName(op.AgentId), await DeploymentName(op.DeploymentId),
                Trigger(run, op.ActorId, op.Trigger), op.State, op.Code, op.CreatedAt, op.StartedAt, op.CompletedAt)
                { ActionRunId = run is not null && await automation.CanReadRun(actor, run, ct) ? run.Id : null });
        }
        // Action runs can wait before they have any low-level operation; list their queue explicitly.
        foreach (var run in runs.Values.Where(x => x.State is "queued" or "running" && (!deployment.HasValue || x.DeploymentId == deployment)))
        {
            if (!await automation.CanReadRun(actor, run, ct)) continue;
            Guid? agent = run.DeploymentId is { } id ? deployments.GetValueOrDefault(id)?.TargetAgentId : null;
            if (agent is null && run.ActionId.HasValue && run.ProtectedPlan.Length > 0)
                agent = automation.Unprotect<ActionPlan>(run.ProtectedPlan).Steps.FirstOrDefault()?.AgentId;
            result.Add(new(run.Id, true, "Action run", await ActionName(run), await AgentName(agent), await DeploymentName(run.DeploymentId),
                Trigger(run, run.RequestedBy, run.Source), run.State, run.Code, run.CreatedAt, run.StartedAt, run.CompletedAt) { ActionRunId = run.Id });
        }
        return result.OrderBy(x => OperationService.Terminal(x.State) || x.CompletedAt.HasValue).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
    }
}
