using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Automation;

public sealed record DeploymentActionButton(Guid Id, string Label, int Order, bool NeedsDialog);
public sealed record DeploymentActionRow(Guid Id, string Name, string State, string Applied, Guid? RunId, string? RunState,
    IReadOnlyList<DeploymentActionButton> Actions);
public sealed record AgentDeploymentSection(Guid Id, string Name, bool Connected, IReadOnlyList<DeploymentActionRow> Deployments);

public sealed class DeploymentActionOverview(StanzaDbContext db, AccessService access, AutomationService automation,
    IAgentCommandDispatcher dispatcher, IDataProtectionProvider protection)
{
    public async Task<IReadOnlyList<AgentDeploymentSection>> ReadAsync(Guid actor, CancellationToken ct)
    {
        var buttons = new Dictionary<Guid, List<DeploymentActionButton>>();
        foreach (var action in await db.Set<ActionDefinition>().AsNoTracking().Where(x => x.Enabled).ToListAsync(ct))
        {
            if (!await access.IsAllowedAsync(actor, "action.execute", "action", action.Id, ct)) continue;
            var plan = automation.Unprotect<ActionPlan>(action.ProtectedSteps);
            if (!plan.ShowOnOverview || plan.DeploymentId is not { } deployment) continue;
            if (!buttons.TryGetValue(deployment, out var list)) buttons[deployment] = list = [];
            list.Add(new(action.Id, plan.ButtonLabel ?? action.Name, plan.Order, plan.Parameters?.Count > 0 || !string.IsNullOrEmpty(plan.Confirmation)));
        }
        var sections = new List<AgentDeploymentSection>();
        foreach (var agent in await db.Set<AgentEntity>().AsNoTracking().Where(a => a.Status == "approved").OrderBy(a => a.Hostname).ToListAsync(ct))
        {
            var rows = new List<DeploymentActionRow>();
            foreach (var deployment in await db.Set<DeploymentDraftEntity>().AsNoTracking().Where(d => d.TargetAgentId == agent.Id).OrderBy(d => d.Name).ToListAsync(ct))
            {
                if (!buttons.ContainsKey(deployment.Id) && !await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment.Id, ct)) continue;
                var runtime = await db.Set<DeploymentRuntimeEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment.Id, ct);
                var available = buttons.GetValueOrDefault(deployment.Id) ?? [];
                var actionIds = available.Select(b => b.Id).ToArray();
                var run = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.ActionId.HasValue && actionIds.Contains(x.ActionId.Value))
                    .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
                var applied = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.DeploymentId == deployment.Id && x.Kind == "deploy" && x.State == "Succeeded")
                    .OrderByDescending(x => x.CompletedAt).FirstOrDefaultAsync(ct);
                var selected = "Not yet deployed";
                if (applied is not null)
                {
                    using var document = JsonDocument.Parse(protection.CreateProtector("Stanza.Operation.v1", applied.Id.ToString("N")).Unprotect(applied.ProtectedRequest));
                    var request = document.RootElement.Deserialize<AgentWorkRequest>()!;
                    var selections = document.RootElement.TryGetProperty("_stanzaActionSelections", out var authored) ? authored.Deserialize<ActionExecutionBindings>() : null;
                    selected = string.Join(" · ", (request.Sources ?? []).Select(s => s.Destination + ": " +
                            (selections?.Sources.GetValueOrDefault(s.Destination) ?? s.Reference).Replace("refs/heads/", "", StringComparison.Ordinal))
                        .Concat((selections?.Images ?? request.ImageOverrides ?? new Dictionary<string, string>()).Select(p => p.Key + ": " + p.Value)));
                    if (selected.Length == 0) selected = "Deployed revision " + applied.Revision;
                }
                rows.Add(new(deployment.Id, deployment.Name, !dispatcher.IsConnected(agent.Id) ? "Agent offline — state unknown" : runtime?.ObservedState ?? "Not yet observed",
                    selected, run?.Id, run?.State, available.OrderBy(x => x.Order).ThenBy(x => x.Label).ToArray()));
            }
            if (rows.Count > 0) sections.Add(new(agent.Id, agent.Hostname, dispatcher.IsConnected(agent.Id), rows));
        }
        return sections;
    }
}
