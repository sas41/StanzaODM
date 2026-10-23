using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Audit;
using Stanza.Core.Automation;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Automation;

public sealed record AutomationOverview(IReadOnlyList<ActionDefinition> Actions, IReadOnlyList<AutomationSchedule> Schedules,
    IReadOnlyList<WebhookDefinition> Webhooks, IReadOnlyList<ImageUpdatePolicy> ImagePolicies, IReadOnlyList<AutomationRun> Runs);
public sealed record AutomationRunDetails(AutomationRun Run, IReadOnlyList<AutomationStepRun> Steps);
public sealed record WebhookSaved(WebhookDefinition Definition, string? Secret)
{
    public override string ToString() => "WebhookSaved { [REDACTED] }";
}

public sealed class AutomationService(StanzaDbContext db, AutomationAuthorization authorization, IDataProtectionProvider protection, TimeProvider clock,
    ActionInputService? inputs = null)
{
    private readonly IDataProtector protector = protection.CreateProtector("Stanza.Automation.v1");
    public string Protect<T>(T value) => protector.Protect(JsonSerializer.Serialize(value, AutomationRules.Json));
    public T Unprotect<T>(string value) => JsonSerializer.Deserialize<T>(protector.Unprotect(value), AutomationRules.Json)
        ?? throw new AutomationException("Automation configuration could not be read.");
    public long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task<AutomationOverview> ListAsync(Guid actor, CancellationToken ct = default)
    {
        var actions = new List<ActionDefinition>();
        foreach (var x in await db.Set<ActionDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            if (await Allowed(actor, "action.read", "action", x.Id, ct)) actions.Add(x);
        var schedules = new List<AutomationSchedule>();
        foreach (var x in await db.Set<AutomationSchedule>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            if (x.ActionId is { } actionId
                ? await Allowed(actor, "action.read", "action", actionId, ct) || await Allowed(actor, "action.manage", "action", actionId, ct)
                : await Allowed(actor, "automation.read", "deployment", x.DeploymentId, ct)) schedules.Add(x);
        var hooks = new List<WebhookDefinition>();
        foreach (var x in await db.Set<WebhookDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            if (await Allowed(actor, "automation.read", "deployment", x.DeploymentId, ct)) hooks.Add(x);
        var policies = new List<ImageUpdatePolicy>();
        foreach (var x in await db.Set<ImageUpdatePolicy>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            if (await Allowed(actor, "automation.read", "deployment", x.DeploymentId, ct)) policies.Add(x);
        var runs = new List<AutomationRun>();
        foreach (var x in await db.Set<AutomationRun>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct))
            if (await CanReadRun(actor, x, ct)) runs.Add(x);
        return new(actions, schedules, hooks, policies, runs);
    }

    public async Task<(ActionDefinition Definition, ActionPlan Plan)> ReadActionAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await authorization.RequireAsync(actor, "action.read", "action", id, ct);
        var action = await ActionAsync(id, ct);
        return (action, Unprotect<ActionPlan>(action.ProtectedSteps));
    }

    public async Task<ActionDefinition> SaveActionAsync(Guid actor, Guid? id, long expectedRevision, string name, bool enabled,
        string concurrency, ActionPlan plan, CancellationToken ct = default)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await SecurityWriteGuard.LockAsync(db, ct);
        AutomationRules.Validate(plan); name = AutomationRules.Name(name);
        if (concurrency is not ("queue" or "reject")) throw new AutomationException("Action concurrency must be queue or reject.");
        await authorization.RequireAsync(actor, "action.manage", "action", id, ct);
        // An editor must hold every target permission, particularly host.execute. Editing assumes ownership;
        // it never lets a less privileged editor mutate work that will run as the former owner.
        await authorization.PlanAsync(actor, plan, ct);
        var value = id is { } existing ? await db.Set<ActionDefinition>().SingleOrDefaultAsync(x => x.Id == existing, ct)
            ?? throw new AutomationException("Action is unavailable.") : new ActionDefinition { Id = Guid.NewGuid() };
        if (id is not null && value.Revision != expectedRevision) throw new AutomationException("The action changed. Reload before saving.");
        value.Name = name; value.OwnerId = actor; value.Enabled = enabled; value.Concurrency = concurrency;
        value.ProtectedSteps = Protect(plan); value.Revision++;
        if (id is null) db.Add(value);
        await RecordAuditAsync(actor, "automation.action.saved", value.Id.ToString(), enabled ? "enabled" : "disabled", ct);
        db.Entry(value).State = EntityState.Detached;
        if (transaction is not null) await transaction.CommitAsync(ct);
        return value;
    }

    public async Task<AutomationSchedule> SaveScheduleAsync(Guid actor, AutomationSchedule input, long expectedRevision, CancellationToken ct = default)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await SecurityWriteGuard.LockAsync(db, ct);
        input.Name = AutomationRules.Name(input.Name);
        await authorization.ScheduleAsync(actor, input.ActionId, input.DeploymentId, ct);
        if (input.Enabled || input.ActionId is null) await ValidateTriggerAsync(actor, input.ActionId, input.DeploymentId, input.Operation, ct);
        if (input.ActionId is { } scheduleAction)
        {
            await ActionAsync(scheduleAction, ct);
            if (input.Enabled) await ScheduledParametersAsync(scheduleAction, ct);
        }
        if (string.IsNullOrWhiteSpace(input.Expression) || input.Expression.Length > 256 || string.IsNullOrWhiteSpace(input.TimeZone) || input.TimeZone.Length > 100)
            throw new AutomationException("Supply a bounded cron expression and timezone.");
        if (input.Overlap is not ("skip" or "queue") || input.MissedRun is not ("skip" or "run-once"))
            throw new AutomationException("Select skip/queue overlap and skip/run-once missed-run policies.");
        var next = Preview(input.Expression, input.TimeZone, 1)[0].ToUnixTimeSeconds();
        var existing = input.Id == Guid.Empty ? null : await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.Id, ct)
            ?? throw new AutomationException("Schedule is unavailable.");
        if (existing is not null)
        {
            await authorization.ScheduleAsync(actor, existing.ActionId, existing.DeploymentId, ct);
            if (existing.Revision != expectedRevision) throw new AutomationException("The schedule changed. Reload before saving.");
        }
        input.Id = existing?.Id ?? Guid.NewGuid(); input.OwnerId = actor; input.Revision = (existing?.Revision ?? 0) + 1; input.NextRunAt = next;
        await SaveReplacement(input, existing, expectedRevision, actor, "automation.schedule.saved", input.Id, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return input;
    }

    public async Task<IReadOnlyList<AutomationSchedule>> ActionSchedulesAsync(Guid actor, Guid actionId, CancellationToken ct = default)
    {
        await authorization.RequireAsync(actor, "action.manage", "action", actionId, ct);
        return await db.Set<AutomationSchedule>().AsNoTracking().Where(x => x.ActionId == actionId).OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<AutomationSchedule> SaveActionScheduleAsync(Guid actor, Guid actionId, Guid id, long revision, string name,
        string expression, string timezone, bool enabled, string overlap, string missedRun, CancellationToken ct = default)
    {
        await authorization.RequireAsync(actor, "action.manage", "action", actionId, ct);
        if (id != Guid.Empty && !await db.Set<AutomationSchedule>().AnyAsync(x => x.Id == id && x.ActionId == actionId, ct))
            throw new AutomationException("That schedule does not belong to this action.");
        var action = await ActionAsync(actionId, ct);
        var plan = Unprotect<ActionPlan>(action.ProtectedSteps);
        return await SaveScheduleAsync(actor, new() { Id = id, ActionId = actionId, DeploymentId = plan.DeploymentId,
            Name = name, Expression = expression, TimeZone = timezone, Enabled = enabled, Overlap = overlap, MissedRun = missedRun }, revision, ct);
    }

    public IReadOnlyList<DateTimeOffset> Preview(string expression, string timezone, int count = 5)
    {
        try { return new CronSchedule(expression).Preview(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timezone), count); }
        catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new AutomationException("Invalid cron expression/timezone, or no occurrence within eight years."); }
    }

    public async Task SetScheduleEnabledAsync(Guid actor, Guid id, bool enabled, CancellationToken ct = default)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await SecurityWriteGuard.LockAsync(db, ct);
        var schedule = await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AutomationException("Schedule is unavailable.");
        await authorization.ScheduleAsync(actor, schedule.ActionId, schedule.DeploymentId, ct);
        if (enabled)
        {
            await authorization.ScheduleAsync(schedule.OwnerId, schedule.ActionId, schedule.DeploymentId, ct);
            await ValidateTriggerAsync(actor, schedule.ActionId, schedule.DeploymentId, schedule.Operation, ct);
            await ValidateTriggerAsync(schedule.OwnerId, schedule.ActionId, schedule.DeploymentId, schedule.Operation, ct);
            if (schedule.ActionId is { } actionId) await ScheduledParametersAsync(actionId, ct);
        }
        var next = Preview(schedule.Expression, schedule.TimeZone, 1)[0].ToUnixTimeSeconds();
        await db.Set<AutomationSchedule>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, enabled)
            .SetProperty(x => x.NextRunAt, next).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        await RecordAuditAsync(actor, "automation.schedule.enabled", id.ToString(), enabled ? "enabled" : "paused", ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<WebhookSaved> SaveWebhookAsync(Guid actor, WebhookDefinition input, long expectedRevision, CancellationToken ct = default)
    {
        input.Name = AutomationRules.Name(input.Name);
        // Browser forms bind empty optional strings to null. Empty ref filters/mapping mean
        // accept all refs/no mapped parameters; they are not malformed configuration.
        input.Refs ??= ""; input.ParameterMapping = string.IsNullOrWhiteSpace(input.ParameterMapping) ? "{}" : input.ParameterMapping;
        await authorization.RequireAsync(actor, "automation.manage", "deployment", input.DeploymentId, ct);
        await ValidateTriggerAsync(actor, input.ActionId, input.DeploymentId, input.Operation, ct);
        WebhookSecurity.ValidateConfiguration(input);
        if (input.ActionId is { } actionId)
            WebhookSecurity.ValidateMapping(input.ParameterMapping, Unprotect<ActionPlan>((await ActionAsync(actionId, ct)).ProtectedSteps));
        else if (input.ParameterMapping != "{}") throw new AutomationException("Parameter mapping requires an action.");
        var existing = input.Id == Guid.Empty ? null : await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.Id, ct)
            ?? throw new AutomationException("Webhook is unavailable.");
        if (existing is not null)
        {
            await authorization.RequireAsync(actor, "automation.manage", "deployment", existing.DeploymentId, ct);
            if (existing.Revision != expectedRevision) throw new AutomationException("The webhook changed. Reload before saving.");
        }
        var secret = existing is null ? WebhookSecurity.NewSecret() : null;
        if (input.Enabled && existing is { ProtectedSecret.Length: 0 })
            throw new AutomationException("This webhook secret was revoked. Rotate its secret before enabling it.");
        input.Id = existing?.Id ?? Guid.NewGuid(); input.OwnerId = actor; input.Revision = (existing?.Revision ?? 0) + 1;
        input.ProtectedSecret = existing?.ProtectedSecret ?? Protect(secret!);
        input.RateWindow = existing?.RateWindow ?? 0; input.RateCount = existing?.RateCount ?? 0;
        await SaveReplacement(input, existing, expectedRevision, actor, "automation.webhook.saved", input.Id, ct); return new(input, secret);
    }

    public async Task<string?> RotateWebhookAsync(Guid actor, Guid id, bool revoke, CancellationToken ct = default)
    {
        var hook = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AutomationException("Webhook is unavailable.");
        await authorization.RequireAsync(actor, "automation.manage", "deployment", hook.DeploymentId, ct);
        var secret = revoke ? null : WebhookSecurity.NewSecret();
        var encrypted = secret is null ? "" : Protect(secret);
        await db.Set<WebhookDefinition>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedSecret, encrypted)
            .SetProperty(x => x.Enabled, !revoke && hook.Enabled).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        await RecordAuditAsync(actor, "automation.webhook.secret", id.ToString(), revoke ? "revoked" : "rotated", ct);
        return secret;
    }

    public async Task<ImageUpdatePolicy> SaveImagePolicyAsync(Guid actor, ImageUpdatePolicy input, long expectedRevision, CancellationToken ct = default)
    {
        input.Name = AutomationRules.Name(input.Name);
        await authorization.RequireAsync(actor, "automation.manage", "deployment", input.DeploymentId, ct);
        await authorization.RequireAsync(actor, "deployment.read", "deployment", input.DeploymentId, ct);
        if (input.Mode is not ("notify-only" or "auto-deploy") || input.FrequencyMinutes is < 1 or > 525600 || input.FailureBackoffMinutes is < 1 or > 525600)
            throw new AutomationException("Use notify-only/auto-deploy and frequencies/backoff of 1–525600 minutes.");
        if (input.Mode == "auto-deploy") await authorization.RequireAsync(actor, "deployment.deploy", "deployment", input.DeploymentId, ct);
        var services = ImageUpdates.ServiceNames(input.Services);
        if (services.Count == 0) throw new AutomationException("Select at least one service to check.");
        var existing = input.Id == Guid.Empty ? null : await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.Id, ct)
            ?? throw new AutomationException("Image policy is unavailable.");
        if (existing is not null)
        {
            await authorization.RequireAsync(actor, "automation.manage", "deployment", existing.DeploymentId, ct);
            if (existing.Revision != expectedRevision) throw new AutomationException("The policy changed. Reload before saving.");
        }
        input.Id = existing?.Id ?? Guid.NewGuid(); input.OwnerId = actor; input.Revision = (existing?.Revision ?? 0) + 1;
        input.Services = string.Join(',', services); input.NextRunAt = Now + input.FrequencyMinutes * 60L;
        input.LastCheckAt = existing?.LastCheckAt; input.LastResult = existing?.LastResult ?? "not-checked"; input.DigestsJson = existing?.DigestsJson ?? "[]";
        input.ActiveRunId = existing?.ActiveRunId;
        await SaveReplacement(input, existing, expectedRevision, actor, "automation.image-policy.saved", input.Id, ct); return input;
    }

    public async Task<AutomationRun> QueueActionAsync(Guid actor, Guid id, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken ct = default, long? expectedRevision = null)
    {
        var action = await ActionAsync(id, ct);
        await ValidateTriggerAsync(actor, id, null, "", ct);
        var plan = Unprotect<ActionPlan>(action.ProtectedSteps);
        return await QueueAsync(actor, plan.DeploymentId, id, id, "manual", "manual:" + Guid.NewGuid(), "", parameters, ct: ct, expectedRevision: expectedRevision);
    }

    public async Task<(ActionDefinition Definition, ActionPlan Plan, IReadOnlyDictionary<string, string> Values)> InvocationAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await ValidateTriggerAsync(actor, id, null, "", ct);
        var definition = await ActionAsync(id, ct);
        var plan = Unprotect<ActionPlan>(definition.ProtectedSteps);
        return (definition, plan, await RememberedParametersAsync(id, plan, ct));
    }

    internal async Task<IReadOnlyDictionary<string, string>> ScheduledParametersAsync(Guid actionId, CancellationToken ct)
    {
        var action = await ActionAsync(actionId, ct);
        var plan = Unprotect<ActionPlan>(action.ProtectedSteps);
        var deployment = plan.DeploymentId ?? Guid.Empty;
        var memory = await db.Set<ActionInputMemory>().AsNoTracking().SingleOrDefaultAsync(x => x.ActionId == actionId && x.DeploymentId == deployment, ct);
        var remembered = memory is null ? null : Unprotect<Dictionary<string, string>>(memory.ProtectedValues);
        // Unlike a modal, an unattended run must not silently replace an incompatible remembered
        // value with a default after an input definition changes. Require a fresh manual selection.
        var supplied = remembered?.Where(x => (plan.Parameters ?? []).Any(p => p.Name == x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return AutomationRules.Parameters(plan, supplied);
    }

    private async Task<IReadOnlyDictionary<string, string>> RememberedParametersAsync(Guid id, ActionPlan plan, CancellationToken ct)
    {
        // Host-only actions have no deployment; reserve the empty scope for their shared inputs.
        var deployment = plan.DeploymentId ?? Guid.Empty;
        var memory = await db.Set<ActionInputMemory>().AsNoTracking().SingleOrDefaultAsync(x => x.ActionId == id && x.DeploymentId == deployment, ct);
        var remembered = memory is not null ? Unprotect<Dictionary<string, string>>(memory.ProtectedValues) : new();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in plan.Parameters ?? [])
        {
            if (remembered.TryGetValue(p.Name, out var previous))
            {
                try { values[p.Name] = AutomationRules.Parameters(new([], [p]), new Dictionary<string, string> { [p.Name] = previous })[p.Name]; }
                catch (AutomationException) { /* Changed input definitions do not inherit incompatible values. */ }
            }
            if (!values.ContainsKey(p.Name) && p.Default is not null) values[p.Name] = p.Default;
        }
        return values;
    }

    public async Task<ActionChoicePage> ChoicesAsync(Guid actor, Guid id, string name, string? search, int page, bool refresh, CancellationToken ct)
    {
        var invocation = await InvocationAsync(actor, id, ct);
        var p = invocation.Plan.Parameters?.SingleOrDefault(p => p.Name == name) ?? throw new AutomationException("Input unavailable.");
        if (page is < 0 or > 199 || search?.Length > 256) throw new AutomationException("Invalid choice search.");
        var all = await (inputs ?? throw new AutomationException("Input discovery unavailable.")).ValuesAsync(invocation.Definition.OwnerId, invocation.Plan, p, refresh, ct);
        await ValidateTriggerAsync(actor, id, null, "", ct);
        var filtered = all.Where(x => x.Contains(search ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = invocation.Values.GetValueOrDefault(name);
        return new(filtered.Skip(page * 50).Take(50).ToArray(), search ?? "", page, filtered.Length > (page + 1) * 50, selected,
            selected is not null && all.Contains(selected, StringComparer.Ordinal));
    }

    public async Task<AutomationRun> RunScheduleAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var s = await db.Set<AutomationSchedule>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationException("Schedule is unavailable.");
        await authorization.ScheduleAsync(actor, s.ActionId, s.DeploymentId, ct);
        await ValidateTriggerAsync(actor, s.ActionId, s.DeploymentId, s.Operation, ct);
        // Manual runs use the initiating identity, never impersonate the schedule owner.
        return await QueueAsync(actor, s.DeploymentId, s.ActionId, s.Id, "manual-schedule", "manual:" + Guid.NewGuid(), s.Operation,
            s.ActionId is { } actionId ? await ScheduledParametersAsync(actionId, ct) : null, ct: ct);
    }

    public async Task<AutomationRun> CheckImagesAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var p = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationException("Image policy is unavailable.");
        await authorization.RequireAsync(actor, "automation.manage", "deployment", p.DeploymentId, ct);
        // A policy check runs as its configured owner; initiating users also need target access.
        await authorization.RequireAsync(actor, "deployment.read", "deployment", p.DeploymentId, ct);
        if (p.Mode == "auto-deploy") await authorization.RequireAsync(actor, "deployment.deploy", "deployment", p.DeploymentId, ct);
        return await QueueAsync(p.OwnerId, p.DeploymentId, null, id, "manual-image-policy", "manual:" + Guid.NewGuid(), "image-check", requestedBy: actor, ct: ct);
    }

    public async Task<AutomationRunDetails> ReadRunAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var run = await db.Set<AutomationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationException("Run is unavailable.");
        if (!await CanReadRun(actor, run, ct)) throw new AutomationAccessException();
        return new(run, await db.Set<AutomationStepRun>().AsNoTracking().Where(x => x.RunId == id).OrderBy(x => x.Index).ToListAsync(ct));
    }

    public async Task CancelRunAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var run = (await ReadRunAsync(actor, id, ct)).Run;
        if (run.ActionId is { } action) await authorization.RequireAsync(actor, "action.execute", "action", action, ct);
        else if (run.DeploymentId is { } deployment) await authorization.RequireAsync(actor, "operations.cancel", "deployment", deployment, ct);
        await db.Set<AutomationRun>().Where(x => x.Id == id && (x.State == "queued" || x.State == "running"))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CancelRequested, true), ct);
        await RecordAuditAsync(actor, "automation.cancel", id.ToString(), "requested", ct);
    }

    public async Task ReconcileRunAsync(Guid actor, Guid id, bool confirmedStopped, CancellationToken ct = default)
    {
        var run = (await ReadRunAsync(actor, id, ct)).Run;
        if (!confirmedStopped || run.State != "uncertain") throw new AutomationException("Confirm that the target state has been externally reconciled and no command remains running.");
        if (run.ActionId is { } action) await authorization.RequireAsync(actor, "action.manage", "action", action, ct);
        if (run.DeploymentId is { } deployment) await authorization.RequireAsync(actor, "operations.cancel", "deployment", deployment, ct);
        if (run.Operation != "image-check") await authorization.PlanAsync(actor, Unprotect<ActionPlan>(run.ProtectedPlan), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Set<AutomationRun>().Where(x => x.Id == id && x.State == "uncertain")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "reconciled").SetProperty(x => x.ReconciledBy, actor)
                .SetProperty(x => x.ReconciledAt, Now).SetProperty(x => x.Code, "externally-reconciled-not-retried"), ct) != 1)
            throw new AutomationException("Run changed. Reload before reconciling.");
        await db.Set<ActionDefinition>().Where(x => x.ActiveRunId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
        await db.Set<ImageUpdatePolicy>().Where(x => x.ActiveRunId == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveRunId, (Guid?)null), ct);
        await RecordAuditAsync(actor, "automation.reconciled", id.ToString(), "external-confirmation-no-replay", ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        var hook = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationException("Webhook is unavailable.");
        await authorization.RequireAsync(actor, "automation.read", "deployment", hook.DeploymentId, ct);
        return await db.Set<WebhookDelivery>().AsNoTracking().Where(x => x.WebhookId == id).OrderByDescending(x => x.ReceivedAt).Take(200).ToListAsync(ct);
    }

    public async Task<AutomationLimits> LimitsAsync(CancellationToken ct = default)
    {
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == "automation.limits", ct);
        return setting is null ? new() : JsonSerializer.Deserialize<AutomationLimits>(setting.Value, AutomationRules.Json) ?? new();
    }

    public async Task SaveLimitsAsync(Guid actor, AutomationLimits limits, CancellationToken ct = default)
    {
        await authorization.RequireAsync(actor, "settings.write", "instance", null, ct); AutomationRules.ValidateLimits(limits);
        var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == "automation.limits", ct);
        if (setting is null) { setting = new() { Key = "automation.limits" }; db.Add(setting); }
        setting.Value = JsonSerializer.Serialize(limits, AutomationRules.Json);
        await RecordAuditAsync(actor, "automation.limits.saved", "instance", "saved", ct); db.Entry(setting).State = EntityState.Detached;
    }

    internal async Task ValidateTriggerAsync(Guid actor, Guid? actionId, Guid? deploymentId, string operation, CancellationToken ct)
    {
        if (actionId is { } action)
        {
            await authorization.RequireAsync(actor, "action.execute", "action", action, ct);
            var definition = await ActionAsync(action, ct);
            if (!definition.Enabled) throw new AutomationException("The action is disabled.");
            var plan = Unprotect<ActionPlan>(definition.ProtectedSteps);
            await authorization.RequireAsync(definition.OwnerId, "action.execute", "action", action, ct);
            await authorization.PlanAsync(definition.OwnerId, plan, ct);
            // Executing a fixed action delegates its owner's approved capabilities, not generic
            // deployment/command permissions to the caller. Editing still requires every target.
        }
        else
        {
            if (!AutomationRules.Operations.Contains(operation) || deploymentId is null || deploymentId == Guid.Empty)
                throw new AutomationException("Select a supported deployment operation or action.");
            await authorization.TargetAsync(actor, new(operation, deploymentId), ct);
        }
    }

    internal async Task<AutomationRun> QueueAsync(Guid owner, Guid? deployment, Guid? action, Guid definition, string source,
        string key, string operation, IReadOnlyDictionary<string, string>? parameters = null, string state = "queued", string code = "", Guid? requestedBy = null, CancellationToken ct = default, long? expectedRevision = null)
    {
        ActionPlan plan;
        ActionDefinition? definitionRow = null;
        ActionExecutionBindings? executionBindings = null;
        if (operation == "image-check" || state != "queued") plan = new([]);
        else if (action is { } a)
        {
            definitionRow = await ActionAsync(a, ct);
            await ValidateTriggerAsync(owner, a, deployment, operation, ct);
            if (expectedRevision.HasValue && expectedRevision != definitionRow.Revision) throw new AutomationException("The action changed. Reload its inputs before running it.");
            plan = Unprotect<ActionPlan>(definitionRow.ProtectedSteps);
            parameters = AutomationRules.Parameters(plan, parameters);
            if ((plan.Parameters ?? []).Any(p => p.Input is "branch" or "version"))
                executionBindings = await (inputs ?? throw new AutomationException("Input discovery unavailable.")).ResolveAsync(definitionRow.OwnerId, plan, parameters, ct);
        }
        else plan = new([new(operation, deployment)]);
        // Remote discovery/resolution above must not hold the queue/security write locks.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await SecurityWriteGuard.LockAsync(db, ct);
        await db.Set<AutomationQueueGuard>().Where(x => x.Id == 1).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        var limits = await LimitsAsync(ct);
        if (state == "queued" && limits.MaximumQueuedRuns > 0 && await db.Set<AutomationRun>().CountAsync(x => x.State == "queued", ct) >= limits.MaximumQueuedRuns)
            throw new AutomationException("The automation queue limit has been reached.");
        if (definitionRow is not null)
        {
            var current = await ActionAsync(definitionRow.Id, ct);
            if (current.Revision != definitionRow.Revision) throw new AutomationException("The action changed. Reload its inputs before running it.");
            await ValidateTriggerAsync(owner, definitionRow.Id, deployment, operation, ct);
            if (source == "manual")
            {
                var inputDeployment = plan.DeploymentId ?? Guid.Empty;
                var memory = await db.Set<ActionInputMemory>().SingleOrDefaultAsync(x => x.ActionId == definitionRow.Id && x.DeploymentId == inputDeployment, ct);
                if (memory is null) { memory = new() { ActionId = definitionRow.Id, DeploymentId = inputDeployment }; db.Add(memory); }
                memory.ActionRevision = definitionRow.Revision; memory.ProtectedValues = Protect(parameters);
            }
        }
        var protectedPlan = Protect(plan);
        if (operation == "image-check")
        {
            var policy = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == definition, ct)
                ?? throw new AutomationException("Image policy is unavailable.");
            protectedPlan = Protect(ImagePolicySnapshot.From(policy));
        }
        var run = new AutomationRun { Id = Guid.NewGuid(), OwnerId = owner, DeploymentId = deployment, ActionId = action,
            RequestedBy = requestedBy ?? owner,
            ActionOwnerId = action is { } actionId ? (await ActionAsync(actionId, ct)).OwnerId : null,
            DefinitionId = definition, Source = source, OccurrenceKey = key, Operation = operation, State = state, Code = code,
            CreatedAt = Now, CompletedAt = state == "queued" ? null : Now, ProtectedPlan = protectedPlan, ProtectedParameters = Protect(parameters ?? new Dictionary<string, string>()),
            ProtectedBindings = Protect(executionBindings) };
        db.Add(run); await RecordAuditAsync(requestedBy ?? owner, "automation.trigger." + source, run.Id.ToString(), state, ct);
        db.Entry(run).State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<ActionInputMemory>().ToArray()) entry.State = EntityState.Detached;
        if (transaction is not null) await transaction.CommitAsync(ct);
        return run;
    }

    internal async Task<ActionDefinition> ActionAsync(Guid id, CancellationToken ct) =>
        await db.Set<ActionDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AutomationException("Action is unavailable.");

    internal (IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, string> Images) ExecutionBindings(
        AutomationRun run, ActionPlan plan, ActionStep step, IReadOnlyDictionary<string, string> values)
    {
        var authored = AutomationRules.Bindings(plan, step.DeploymentId, step.Kind, values);
        if (authored.Sources.Count == 0 && authored.Images.Count == 0) return authored;
        var pinned = Unprotect<ActionExecutionBindings>(run.ProtectedBindings);
        return (pinned.Sources, pinned.Images);
    }

    private async Task<bool> Allowed(Guid actor, string permission, string type, Guid? id, CancellationToken ct)
    {
        try { await authorization.RequireAsync(actor, permission, type, id, ct); return true; }
        catch (AutomationAccessException) { return false; }
    }
    private async Task<bool> CanReadRun(Guid actor, AutomationRun run, CancellationToken ct) => run.ActionId is { } a
        ? await Allowed(actor, "action.read", "action", a, ct) || await Allowed(actor, "action.execute", "action", a, ct)
        : await Allowed(actor, "automation.read", "deployment", run.DeploymentId, ct);

    internal async Task RecordAuditAsync(Guid actor, string action, string resource, string result, CancellationToken ct)
    {
        var entry = new AuditEvent { Id = Guid.NewGuid(), ActorId = actor, Action = action, Resource = resource, Result = result, At = Now };
        db.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        finally { db.Entry(entry).State = EntityState.Detached; }
    }

    private async Task SaveReplacement<T>(T input, T? existing, long revision, Guid actor, string action, Guid id, CancellationToken ct) where T : class
    {
        if (existing is null) db.Add(input);
        else
        {
            db.Attach(input); db.Entry(input).State = EntityState.Modified; db.Entry(input).Property("Revision").OriginalValue = revision;
            // Runtime claims/results are not editable form fields and may change without a config revision.
            if (input is ImageUpdatePolicy)
                foreach (var property in new[] { "ActiveRunId", "LastCheckAt", "LastResult", "DigestsJson" }) db.Entry(input).Property(property).IsModified = false;
            if (input is WebhookDefinition)
                foreach (var property in new[] { "RateWindow", "RateCount" }) db.Entry(input).Property(property).IsModified = false;
        }
        try { await RecordAuditAsync(actor, action, id.ToString(), "saved", ct); }
        catch (DbUpdateConcurrencyException) { throw new AutomationException("The definition changed. Reload before saving."); }
        finally { db.Entry(input).State = EntityState.Detached; }
    }
}
