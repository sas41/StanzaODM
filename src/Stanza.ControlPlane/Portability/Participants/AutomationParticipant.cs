using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Portability.Participants;

public sealed record PortableActionStep(string Kind, string? Deployment, string? Agent, AgentExecution? Execution, int TimeoutSeconds, string Failure)
{
    public override string ToString() => "Portable action step (execution inputs redacted)";
}
public sealed record PortableAction(string Id, string Owner, string Name, string Concurrency, bool Enabled,
    IReadOnlyList<PortableActionStep> Steps, IReadOnlyList<ActionParameter>? Parameters,
    string? Deployment = null, string? ButtonLabel = null, int Order = 0, string? Confirmation = null, bool ShowOnOverview = true);
public sealed record PortableSchedule(string Owner, string? Deployment, string? Action, string Operation, string Name,
    string Expression, string TimeZone, string Overlap, string MissedRun, bool Enabled);
public sealed record PortableWebhook(string Owner, string Deployment, string? Action, string Operation, string Name,
    string Provider, string Events, string Refs, string ParameterMapping, string? Secret, bool Enabled)
{
    public override string ToString() => "Portable webhook (secret redacted)";
}
public sealed record PortableImagePolicy(string Owner, string Deployment, string Name, string Services, string Mode,
    int FrequencyMinutes, int FailureBackoffMinutes, bool Enabled);
public sealed record PortableAutomation(IReadOnlyList<PortableAction> Actions, IReadOnlyList<PortableSchedule> Schedules,
    IReadOnlyList<PortableWebhook> Webhooks, IReadOnlyList<PortableImagePolicy> ImageUpdates);

public sealed class AutomationParticipant(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization,
    ParticipantSupport support) : IPortableConfigurationParticipant
{
    public string Kind => "automation";
    public int Priority => 50;
    public IReadOnlyList<PortableResourceReference> DefinedResources(JsonElement content) =>
        ParticipantSupport.Read<PortableAutomation>(content).Actions.Select(a => new PortableResourceReference(a.Id, "action", a.Name)).ToArray();

    private async Task<AutomationOverview> Selected(PortableParticipantContext c, CancellationToken ct)
    {
        var all = await automation.ListAsync(c.ActorId, ct);
        bool Included(Guid id) => c.DeploymentIds.Count == 0 || c.DeploymentIds.Contains(id);
        var schedules = all.Schedules.Where(s => c.DeploymentIds.Count == 0 || s.DeploymentId is { } d && Included(d)).ToArray();
        var hooks = all.Webhooks.Where(s => Included(s.DeploymentId)).ToArray();
        var actions = new Dictionary<Guid, ActionDefinition>();
        foreach (var a in all.Actions)
        {
            var read = await automation.ReadActionAsync(c.ActorId, a.Id, ct);
            if (c.DeploymentIds.Count == 0 || read.Plan.DeploymentId is { } button && Included(button) ||
                !c.DeploymentClone && read.Plan.Steps.Any(s => s.DeploymentId is { } id && Included(id))) actions[a.Id] = a;
        }
        foreach (var id in schedules.Select(s => s.ActionId).Concat(hooks.Select(h => h.ActionId)).Where(x => x.HasValue).Select(x => x!.Value))
            if (!c.DeploymentClone || actions.ContainsKey(id)) actions[id] = (await automation.ReadActionAsync(c.ActorId, id, ct)).Definition;
        schedules = all.Schedules.Where(s => c.DeploymentIds.Count == 0 || s.DeploymentId is { } d && Included(d) || s.ActionId is { } a && actions.ContainsKey(a)).ToArray();
        if (c.DeploymentClone)
        {
            schedules = schedules.Where(s => s.ActionId is null || actions.ContainsKey(s.ActionId.Value)).ToArray();
            hooks = hooks.Where(h => h.ActionId is null || actions.ContainsKey(h.ActionId.Value)).ToArray();
        }
        return new(actions.Values.ToArray(), schedules, hooks, all.ImagePolicies.Where(s => Included(s.DeploymentId)).ToArray(), []);
    }
    public async Task<IReadOnlyList<PortableResourceCandidate>> CollectReferencesAsync(PortableParticipantContext c, CancellationToken ct)
    {
        var selected = await Selected(c, ct);
        var result = new List<PortableResourceCandidate>();
        var deployments = new HashSet<Guid>(selected.Schedules.Where(x => x.DeploymentId.HasValue).Select(x => x.DeploymentId!.Value)
            .Concat(selected.Webhooks.Select(x => x.DeploymentId)).Concat(selected.ImagePolicies.Select(x => x.DeploymentId)));
        var agents = new HashSet<Guid>();
        foreach (var action in selected.Actions)
        {
            var read = await automation.ReadActionAsync(c.ActorId, action.Id, ct);
            result.Add(new(action.Id, "action", action.Name));
            if (read.Plan.DeploymentId is { } buttonDeployment) deployments.Add(buttonDeployment);
            foreach (var s in read.Plan.Steps)
            { if (s.DeploymentId is { } d) deployments.Add(d); if (s.AgentId is { } a) agents.Add(a); }
        }
        foreach (var id in deployments)
        {
            await support.Require(c.ActorId, "deployment.read", "deployment", id, ct);
            result.Add(new(id, "deployment", await db.Set<DeploymentDraftEntity>().Where(x => x.Id == id).Select(x => x.Name).SingleAsync(ct)));
        }
        foreach (var id in agents)
        {
            await support.Require(c.ActorId, "agent.read", "agent", id, ct);
            result.Add(new(id, "agent", await db.Set<AgentEntity>().Where(x => x.Id == id).Select(x => x.Hostname).SingleAsync(ct)));
        }
        return result;
    }
    public async Task<JsonElement> ExportAsync(PortableParticipantContext c, CancellationToken ct)
    {
        var selected = await Selected(c, ct);
        var actions = new List<PortableAction>();
        foreach (var a in selected.Actions)
        {
            var read = await automation.ReadActionAsync(c.ActorId, a.Id, ct);
            actions.Add(new(ParticipantSupport.Reference(c, a.Id, "action"), await support.Username(a.OwnerId, ct), a.Name, a.Concurrency, a.Enabled,
                read.Plan.Steps.Select(s => new PortableActionStep(s.Kind, s.DeploymentId is { } d ? ParticipantSupport.Reference(c, d, "deployment") : null,
                    s.AgentId is { } agent ? ParticipantSupport.Reference(c, agent, "agent") : null, s.Execution, s.TimeoutSeconds, s.Failure)).ToArray(), read.Plan.Parameters,
                read.Plan.DeploymentId is { } display ? ParticipantSupport.Reference(c, display, "deployment") : null,
                read.Plan.ButtonLabel, read.Plan.Order, read.Plan.Confirmation, read.Plan.ShowOnOverview));
        }
        var schedules = new List<PortableSchedule>();
        foreach (var s in selected.Schedules)
            schedules.Add(new(await support.Username(s.OwnerId, ct), s.DeploymentId is { } d ? ParticipantSupport.Reference(c, d, "deployment") : null,
                s.ActionId is { } a ? ParticipantSupport.Reference(c, a, "action") : null, s.Operation, s.Name, s.Expression, s.TimeZone, s.Overlap, s.MissedRun, s.Enabled));
        var hooks = new List<PortableWebhook>();
        foreach (var h in selected.Webhooks)
        {
            await support.Require(c.ActorId, "automation.manage", "deployment", h.DeploymentId, ct);
            hooks.Add(new(await support.Username(h.OwnerId, ct), ParticipantSupport.Reference(c, h.DeploymentId, "deployment"),
                h.ActionId is { } a ? ParticipantSupport.Reference(c, a, "action") : null, h.Operation, h.Name, h.Provider, h.Events, h.Refs, h.ParameterMapping,
                string.IsNullOrEmpty(h.ProtectedSecret) ? null : automation.Unprotect<string>(h.ProtectedSecret), h.Enabled));
        }
        var policies = new List<PortableImagePolicy>();
        foreach (var p in selected.ImagePolicies)
            policies.Add(new(await support.Username(p.OwnerId, ct), ParticipantSupport.Reference(c, p.DeploymentId, "deployment"), p.Name, p.Services,
                p.Mode, p.FrequencyMinutes, p.FailureBackoffMinutes, p.Enabled));
        return ParticipantSupport.Json(new PortableAutomation(actions, schedules, hooks, policies));
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await support.Require(c.ActorId, "configuration.import", "instance", null, ct);
        var section = ParticipantSupport.Read<PortableAutomation>(content);
        ParticipantSupport.Bound(section.Actions.Count); ParticipantSupport.Bound(section.Schedules.Count);
        ParticipantSupport.Bound(section.Webhooks.Count); ParticipantSupport.Bound(section.ImageUpdates.Count);
        if (section.Actions.Select(a => a.Id).Distinct().Count() != section.Actions.Count) throw new PortabilityException("Duplicate action definitions.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in section.Actions)
        {
            AutomationRules.Name(a.Name);
            var normalized = AutomationRules.NormalizeName(a.Name);
            if (!names.Add((a.Deployment ?? "global") + ":" + normalized)) throw new PortabilityException("Duplicate action names within an imported scope.");
            if (a.Deployment is null && await db.Set<ActionDefinition>().AnyAsync(x => x.ScopeDeploymentId == null && x.NormalizedName == normalized, ct))
                throw new PortabilityException("A Global action with that name already exists. Rename it before importing.");
            if (a.Concurrency is not ("queue" or "reject")) throw new PortabilityException("Invalid action concurrency policy.");
            var owner = await support.Owner(c, a.Owner, ct);
            await support.Require(c.ActorId, "action.manage", "action", null, ct);
            await support.Require(owner, "action.manage", "action", null, ct);
            var plan = await Plan(a, c, true, ct);
            AutomationRules.Validate(plan);
            for (var i = 0; i < a.Steps.Count; i++)
            {
                var s = a.Steps[i];
                var target = s.Kind == "host-exec" ? await support.Resolve(c, s.Agent!, "agent", ct, true) : await support.Resolve(c, s.Deployment!, "deployment", ct, true);
                var permission = Permission(s.Kind);
                await support.Require(c.ActorId, permission, s.Kind == "host-exec" ? "agent" : "deployment", target, ct);
                await support.Require(owner, permission, s.Kind == "host-exec" ? "agent" : "deployment", target, ct);
            }
        }
        foreach (var s in section.Schedules)
        {
            AutomationRules.Name(s.Name); automation.Preview(s.Expression, s.TimeZone, 1);
            if (s.Overlap is not ("skip" or "queue") || s.MissedRun is not ("skip" or "run-once")) throw new PortabilityException("Invalid scheduling policies.");
            await Trigger(c, section, s.Owner, s.Deployment, s.Action, s.Operation, ct, actionSchedule: s.Action is not null);
            // Imported schedules are paused and have no shared input memory yet. Required inputs
            // are checked when an operator enables the schedule after a manual run/default setup.
        }
        foreach (var h in section.Webhooks)
        {
            AutomationRules.Name(h.Name); WebhookSecurity.ValidateConfiguration(Hook(h, Guid.NewGuid(), null));
            if (h.Secret is not null && (h.Secret.Length is < 32 or > 16384 || h.Secret.Any(char.IsControl))) throw new PortabilityException("Invalid plaintext webhook secret.");
            await Trigger(c, section, h.Owner, h.Deployment, h.Action, h.Operation, ct);
            if (h.Action is { } a) WebhookSecurity.ValidateMapping(h.ParameterMapping, await ActionPlan(c, section, a, true, ct));
            else if (h.ParameterMapping != "{}") throw new PortabilityException("Webhook parameter mappings require an action.");
        }
        foreach (var p in section.ImageUpdates)
        {
            AutomationRules.Name(p.Name);
            if (p.Mode is not ("notify-only" or "auto-deploy") || p.FrequencyMinutes is < 1 or > 525600 || p.FailureBackoffMinutes is < 1 or > 525600 || ImageUpdates.ServiceNames(p.Services).Count == 0)
                throw new PortabilityException("Invalid image update policy.");
            await Trigger(c, section, p.Owner, p.Deployment, null, p.Mode == "auto-deploy" ? "deploy" : "status", ct);
        }
        return [];
    }
    private async Task Trigger(PortableParticipantImportContext c, PortableAutomation section, string username, string? deploymentKey, string? actionKey, string operation, CancellationToken ct, bool actionSchedule = false)
    {
        var owner = await support.Owner(c, username, ct);
        var deployment = deploymentKey is { } key ? await support.Resolve(c, key, "deployment", ct, true) : null;
        if (!actionSchedule)
        {
            if (deploymentKey is null) throw new PortabilityException("Deployment operations and webhooks require a deployment.");
            await support.Require(c.ActorId, "automation.manage", "deployment", deployment, ct);
            await support.Require(owner, "automation.manage", "deployment", deployment, ct);
        }
        if (actionKey is { } a)
        {
            var id = await support.Resolve(c, a, "action", ct, true);
            if (actionSchedule)
            {
                await support.Require(c.ActorId, "action.manage", "action", id, ct);
                await support.Require(owner, "action.manage", "action", id, ct);
            }
            await support.Require(c.ActorId, "action.execute", "action", id, ct); await support.Require(owner, "action.execute", "action", id, ct);
            var definition = section.Actions.SingleOrDefault(x => x.Id == a);
            if (definition is not null)
            {
                var actionOwner = await support.Owner(c, definition.Owner, ct);
                await support.Require(actionOwner, "action.execute", "action", id, ct);
                    // Importing a trigger cannot broaden the importing editor's capabilities.
                for (var i = 0; i < definition.Steps.Count; i++)
                {
                    var step = definition.Steps[i];
                    var type = step.Kind == "host-exec" ? "agent" : "deployment";
                    var target = await support.Resolve(c, type == "agent" ? step.Agent! : step.Deployment!, type, ct, true);
                    if (!actionSchedule) await support.Require(owner, Permission(step.Kind), type, target, ct);
                }
            }
            else if (id is { } existing)
            {
                var plan = (await automation.ReadActionAsync(c.ActorId, existing, ct)).Plan;
                await authorization.PlanAsync(c.ActorId, plan, ct);
                if (!actionSchedule) await authorization.PlanAsync(owner, plan, ct);
            }
        }
        else
        {
            if (!AutomationRules.Operations.Contains(operation)) throw new PortabilityException("Unknown automation operation.");
            await support.Require(c.ActorId, Permission(operation), "deployment", deployment, ct);
            await support.Require(owner, Permission(operation), "deployment", deployment, ct);
        }
    }
    private async Task<ActionPlan> ActionPlan(PortableParticipantImportContext c, PortableAutomation section, string key, bool preview, CancellationToken ct)
    {
        var action = section.Actions.SingleOrDefault(a => a.Id == key);
        return action is not null ? await Plan(action, c, preview, ct)
            : (await automation.ReadActionAsync(c.ActorId, (await support.Resolve(c, key, "action", ct))!.Value, ct)).Plan;
    }
    private async Task<ActionPlan> Plan(PortableAction action, PortableParticipantImportContext c, bool preview, CancellationToken ct)
    {
        var steps = new List<ActionStep>();
        var deploymentIds = new Dictionary<string, Guid>();
        foreach (var step in action.Steps)
        {
            Guid? deployment = null;
            if (step.Deployment is { } d)
            {
                if (!deploymentIds.TryGetValue(d, out var mapped)) deploymentIds[d] = mapped = await support.Resolve(c, d, "deployment", ct, preview) ?? Guid.NewGuid();
                deployment = mapped;
            }
            Guid? agent = step.Agent is { } a ? await support.Resolve(c, a, "agent", ct, preview) ?? Guid.NewGuid() : null;
            steps.Add(new(step.Kind, deployment, agent, step.Execution, step.TimeoutSeconds, step.Failure));
        }
        return new(steps, action.Parameters, action.Deployment is { } display ? deploymentIds.GetValueOrDefault(display,
                await support.Resolve(c, display, "deployment", ct, preview) ?? Guid.NewGuid()) : null,
            action.ButtonLabel, action.Order, action.Confirmation, action.ShowOnOverview);
    }
    private static string Permission(string kind) => kind switch
    {
        "host-exec" => "host.execute", "container-exec" => "container.execute", "pull" => "deployment.deploy",
        "status" => "deployment.read", "logs" => "logs.read", _ => "deployment." + kind
    };
    private static WebhookDefinition Hook(PortableWebhook h, Guid deployment, Guid? action) => new()
    { DeploymentId = deployment, ActionId = action, Operation = h.Operation, Name = h.Name, Provider = h.Provider, Events = h.Events,
        Refs = h.Refs, ParameterMapping = h.ParameterMapping, Enabled = false };

    public async Task ApplyAsync(JsonElement content, PortableParticipantImportContext c, CancellationToken ct)
    {
        await ValidateAsync(content, c, ct);
        var section = ParticipantSupport.Read<PortableAutomation>(content);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        await ValidateAsync(content, c, ct);
        var actions = new List<(PortableAction Definition, ActionDefinition Saved, Guid Owner, ActionPlan Plan)>();
        try
        {
            foreach (var a in section.Actions)
            {
                var owner = await support.Owner(c, a.Owner, ct);
                var plan = await Plan(a, c, false, ct);
                // Enabled only inside this transaction to satisfy trigger validation. No worker can observe it;
                // all imported actions are disabled before the transaction is committed.
                var saved = await automation.SaveActionAsync(owner, null, 0, a.Name, true, a.Concurrency, plan, ct);
                actions.Add((a, saved, owner, plan)); c.CreatedResources![a.Id] = saved.Id;
            }
            foreach (var s in section.Schedules)
            {
                var owner = await support.Owner(c, s.Owner, ct);
                await automation.SaveScheduleAsync(owner, new() { DeploymentId = s.Deployment is { } d ? await support.Resolve(c, d, "deployment", ct) : null,
                    ActionId = s.Action is { } a ? await support.Resolve(c, a, "action", ct) : null, Name = s.Name, Operation = s.Operation,
                    Expression = s.Expression, TimeZone = s.TimeZone, Overlap = s.Overlap, MissedRun = s.MissedRun, Enabled = false }, 0, ct);
            }
            foreach (var h in section.Webhooks)
            {
                var owner = await support.Owner(c, h.Owner, ct);
                var saved = await automation.SaveWebhookAsync(owner, Hook(h, (await support.Resolve(c, h.Deployment, "deployment", ct))!.Value,
                    h.Action is { } a ? await support.Resolve(c, a, "action", ct) : null), 0, ct);
                // Configuration was validated by the service; restore only its separately validated plaintext HMAC secret.
                var secret = h.Secret is null ? "" : automation.Protect(h.Secret);
                await db.Set<WebhookDefinition>().Where(x => x.Id == saved.Definition.Id && !x.Enabled)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedSecret, secret), ct);
            }
            foreach (var p in section.ImageUpdates)
                await automation.SaveImagePolicyAsync(await support.Owner(c, p.Owner, ct), new() {
                    DeploymentId = (await support.Resolve(c, p.Deployment, "deployment", ct))!.Value, Name = p.Name, Services = p.Services,
                    Mode = p.Mode, FrequencyMinutes = p.FrequencyMinutes, FailureBackoffMinutes = p.FailureBackoffMinutes, Enabled = false }, 0, ct);
            foreach (var a in actions)
                await automation.SaveActionAsync(a.Owner, a.Saved.Id, a.Saved.Revision, a.Definition.Name, false, a.Definition.Concurrency, a.Plan, ct);
            await automation.RecordAuditAsync(c.ActorId, "configuration.automation.import", "instance", "disabled-owner-mapped", ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            foreach (var action in actions) c.CreatedResources?.Remove(action.Definition.Id);
            throw;
        }
    }
}
