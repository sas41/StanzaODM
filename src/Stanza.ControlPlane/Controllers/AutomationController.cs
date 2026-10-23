using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Models;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("automation"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(256 * 1024)]
public sealed class AutomationController(AutomationService automation, AutomationAuthorization authorization, Sources.SourceService sources,
    Persistence.StanzaDbContext db, Security.AccessService access) : Controller
{
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await automation.ListAsync(Actor, ct));

    [HttpGet("actions/edit")]
    public async Task<IActionResult> ActionEditor(Guid? id, CancellationToken ct)
    {
        try
        {
            await authorization.RequireAsync(Actor, "action.manage", "action", id, ct);
            ViewData["ActionDeployments"] = await sources.ListDeploymentsAsync(Actor, ct);
            ViewData["ActionAgents"] = await sources.ListAgentsAsync(Actor, ct);
            if (id is null) return View(new ActionEditorModel());
            var (definition, plan) = await automation.ReadActionAsync(Actor, id.Value, ct);
            ViewData["ActionSchedules"] = await automation.ActionSchedulesAsync(Actor, id.Value, ct);
            return View(new ActionEditorModel { Id = id, Name = definition.Name, Enabled = definition.Enabled, Revision = definition.Revision,
                Concurrency = definition.Concurrency, PlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions(AutomationRules.Json) { WriteIndented = true }) });
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (AutomationException) { return NotFound(); }
    }

    [HttpPost("actions/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ActionEditor(ActionEditorModel model, bool configureSchedule, CancellationToken ct)
    {
        try
        {
            await authorization.RequireAsync(Actor, "action.manage", "action", model.Id, ct);
            ViewData["ActionDeployments"] = await sources.ListDeploymentsAsync(Actor, ct);
            ViewData["ActionAgents"] = await sources.ListAgentsAsync(Actor, ct);
            if (model.Id is { } actionId) ViewData["ActionSchedules"] = await automation.ActionSchedulesAsync(Actor, actionId, ct);
            if (!ModelState.IsValid) return View(model);
            var saved = await automation.SaveActionAsync(Actor, model.Id, model.Revision, model.Name, model.Enabled, model.Concurrency, AutomationRules.ParsePlan(model.PlanJson), ct);
            if (configureSchedule) return RedirectToAction(nameof(ActionEditor), new { id = saved.Id });
            return RedirectToAction(nameof(Index));
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e)) { ModelState.AddModelError("", Message(e)); return View(model); }
    }

    [HttpPost("actions/{actionId:guid}/schedules"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ActionSchedule(Guid actionId, ScheduleEditorModel model, CancellationToken ct)
    {
        try
        {
            await authorization.RequireAsync(Actor, "action.manage", "action", actionId, ct);
            if (!ModelState.IsValid) throw new AutomationException("Check the schedule name, cron expression, and timezone.");
            await automation.SaveActionScheduleAsync(Actor, actionId, model.Id, model.Revision, model.Name, model.Expression,
                model.TimeZone, model.Enabled, model.Overlap, model.MissedRun, ct);
            TempData["Success"] = model.Enabled ? "Action schedule enabled. Each occurrence uses the shared last-used inputs." : "Action schedule saved and paused.";
            return RedirectToAction(nameof(ActionEditor), new { id = actionId });
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e))
        { TempData["Error"] = Message(e); return RedirectToAction(nameof(ActionEditor), new { id = actionId }); }
    }

    [HttpPost("actions/{id:guid}/run"), ValidateAntiForgeryToken]
    public Task<IActionResult> RunAction(Guid id, string? parametersJson, bool confirmed, CancellationToken ct) => Mutate(async () =>
    {
        Dictionary<string, string>? values;
        try { values = string.IsNullOrWhiteSpace(parametersJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson, AutomationRules.Json); }
        catch (JsonException) { throw new AutomationException("Parameters must be a JSON object with string values."); }
        var invocation = await automation.InvocationAsync(Actor, id, ct);
        if (!string.IsNullOrEmpty(invocation.Plan.Confirmation) && !confirmed) return RedirectToAction(nameof(Invoke), new { id });
        var run = await automation.QueueActionAsync(Actor, id, values, ct, invocation.Definition.Revision); return RedirectToAction(nameof(Run), new { id = run.Id });
    });

    [HttpGet("actions/{id:guid}/invoke")]
    public async Task<IActionResult> Invoke(Guid id, CancellationToken ct)
    {
        try
        {
            var x = await automation.InvocationAsync(Actor, id, ct);
            var model = new ActionInvocationModel(id, x.Definition.Revision, x.Plan.ButtonLabel ?? x.Definition.Name, x.Plan, x.Values);
            return Request.Headers["HX-Request"] == "true" ? PartialView("_Invoke", model) : View(model);
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (AutomationException e) { return BadRequest(e.Message); }
    }

    [Authorize(Roles = "Admin,SystemAdmin"), HttpGet("actions/{id:guid}/access")]
    public async Task<IActionResult> ActionAccess(Guid id, CancellationToken ct)
    {
        var action = await automation.ReadActionAsync(Actor, id, ct);
        var subjects = (await db.Accounts.AsNoTracking().Where(x => x.Role == Core.Security.AccountRole.User).OrderBy(x => x.Username).ToListAsync(ct))
            .Select(x => new ActionAccessSubject(x.Id, x.Username, false)).Concat((await access.ListGroupsAsync(ct)).Select(x => new ActionAccessSubject(x.Id, x.Name, true))).ToArray();
        var grants = await db.Grants.AsNoTracking().Where(x => x.ResourceType == "action" && x.ResourceId == id && x.Action == "action.execute").ToListAsync(ct);
        return View(new ActionAccessModel(id, action.Definition.Name, subjects, grants.Select(g => new Security.GrantSummary(g.Id, g.AccountId, g.GroupId, g.Action, g.ResourceType, g.ResourceId)).ToArray()));
    }

    [Authorize(Roles = "Admin,SystemAdmin"), HttpPost("actions/{id:guid}/access"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ActionAccess(Guid id, Guid? accountId, Guid? groupId, Guid? removeGrantId, CancellationToken ct)
    {
        await authorization.RequireAsync(Actor, "action.manage", "action", id, ct);
        try
        {
            if (removeGrantId is { } grant)
            {
                if (!await db.Grants.AnyAsync(x => x.Id == grant && x.ResourceType == "action" && x.ResourceId == id && x.Action == "action.execute", ct)) return BadRequest();
                await access.RemoveGrantAsync(Actor, grant, ct);
            }
            else
            {
                await automation.ReadActionAsync(Actor, id, ct);
                await access.AddGrantAsync(Actor, accountId, groupId, "action.execute", "action", id, ct);
            }
        }
        catch (Security.SecurityOperationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(ActionAccess), new { id });
    }

    [HttpGet("actions/{id:guid}/choices")]
    public async Task<IActionResult> Choices(Guid id, string name, string? search, int page, bool refresh, CancellationToken ct)
    {
        try { return Json(await automation.ChoicesAsync(Actor, id, name, search, page, refresh, ct)); }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (e is AutomationException or Sources.SourceException or Sources.SourceAccessException or Transport.AgentUnavailableException)
        { return BadRequest(new { error = e is AutomationException ? e.Message : "Remote choices are unavailable. Check the Agent and repository credentials." }); }
    }

    [HttpPost("actions/{id:guid}/submit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id, [FromForm(Name = "values")] Dictionary<string, string>? values, long? revision, bool confirmed, CancellationToken ct)
    {
        try
        {
            var x = await automation.InvocationAsync(Actor, id, ct);
            if (!string.IsNullOrEmpty(x.Plan.Confirmation) && !confirmed) throw new AutomationException("Confirm this action before running it.");
            if (revision.HasValue && revision != x.Definition.Revision) throw new AutomationException("The action changed. Review its current inputs before running it.");
            var run = await automation.QueueActionAsync(Actor, id, values, ct, revision ?? x.Definition.Revision);
            return RedirectToAction(nameof(Run), new { id = run.Id });
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (e is AutomationException or Sources.SourceException or Sources.SourceAccessException or DbUpdateException or Transport.AgentUnavailableException)
        {
            var x = await automation.InvocationAsync(Actor, id, ct);
            return View("Invoke", new ActionInvocationModel(id, x.Definition.Revision, x.Plan.ButtonLabel ?? x.Definition.Name, x.Plan, values ?? new(),
                e is AutomationException ? e.Message : "The action could not be queued. Check repository access and try again."));
        }
    }

    [HttpGet("schedules/edit")]
    public async Task<IActionResult> ScheduleEditor(Guid? id, CancellationToken ct)
    {
        if (id is null) return View(new ScheduleEditorModel());
        var x = (await automation.ListAsync(Actor, ct)).Schedules.SingleOrDefault(x => x.Id == id);
        return x is null ? Forbid() : View(new ScheduleEditorModel { Id = x.Id, Revision = x.Revision, Name = x.Name, DeploymentId = x.DeploymentId,
            ActionId = x.ActionId, Operation = x.Operation, Expression = x.Expression, TimeZone = x.TimeZone, Enabled = x.Enabled, Overlap = x.Overlap, MissedRun = x.MissedRun });
    }

    [HttpPost("schedules/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ScheduleEditor(ScheduleEditorModel model, CancellationToken ct)
    {
        try
        {
            await authorization.ScheduleAsync(Actor, model.ActionId, model.DeploymentId, ct);
            if (!ModelState.IsValid) return View(model);
            await automation.SaveScheduleAsync(Actor, new() { Id = model.Id, Name = model.Name, DeploymentId = model.DeploymentId, ActionId = model.ActionId,
                Operation = model.Operation, Expression = model.Expression, TimeZone = model.TimeZone, Enabled = model.Enabled, Overlap = model.Overlap, MissedRun = model.MissedRun }, model.Revision, ct);
            return RedirectToAction(nameof(Index));
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e)) { ModelState.AddModelError("", Message(e)); return View(model); }
    }

    [HttpPost("schedules/preview"), ValidateAntiForgeryToken]
    public IActionResult Preview(string expression, string timezone)
    {
        try { return PartialView("Occurrences", automation.Preview(expression, timezone)); }
        catch (AutomationException e) { return BadRequest(e.Message); }
    }

    [HttpPost("schedules/{id:guid}/enabled"), ValidateAntiForgeryToken]
    public Task<IActionResult> ScheduleEnabled(Guid id, bool enabled, CancellationToken ct) => Mutate(async () =>
    { await automation.SetScheduleEnabledAsync(Actor, id, enabled, ct); return RedirectToAction(nameof(Index)); });

    [HttpPost("schedules/{id:guid}/run"), ValidateAntiForgeryToken]
    public Task<IActionResult> RunSchedule(Guid id, CancellationToken ct) => Mutate(async () =>
    { var run = await automation.RunScheduleAsync(Actor, id, ct); return RedirectToAction(nameof(Run), new { id = run.Id }); });

    [HttpGet("webhooks/edit")]
    public async Task<IActionResult> WebhookEditor(Guid? id, CancellationToken ct)
    {
        if (id is null) return View(new WebhookEditorModel());
        var x = (await automation.ListAsync(Actor, ct)).Webhooks.SingleOrDefault(x => x.Id == id);
        return x is null ? Forbid() : View(new WebhookEditorModel { Id = x.Id, Revision = x.Revision, Name = x.Name, DeploymentId = x.DeploymentId,
            ActionId = x.ActionId, Operation = x.Operation, Provider = x.Provider, Events = x.Events, Refs = x.Refs, ParameterMapping = x.ParameterMapping, Enabled = x.Enabled });
    }

    [HttpPost("webhooks/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> WebhookEditor(WebhookEditorModel model, CancellationToken ct)
    {
        try
        {
            await authorization.RequireAsync(Actor, "automation.manage", "deployment", model.DeploymentId, ct);
            if (!ModelState.IsValid) return View(model);
            var result = await automation.SaveWebhookAsync(Actor, new() { Id = model.Id, Name = model.Name, DeploymentId = model.DeploymentId,
                ActionId = model.ActionId, Operation = model.Operation, Provider = model.Provider, Events = model.Events, Refs = model.Refs,
                ParameterMapping = model.ParameterMapping, Enabled = model.Enabled }, model.Revision, ct);
            return result.Secret is null ? RedirectToAction(nameof(Index)) : View("Secret", new WebhookSecretModel(result.Definition.Id, result.Secret));
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e)) { ModelState.AddModelError("", Message(e)); return View(model); }
    }

    [HttpPost("webhooks/{id:guid}/secret"), ValidateAntiForgeryToken]
    public Task<IActionResult> RotateSecret(Guid id, bool revoke, CancellationToken ct) => Mutate(async () =>
    {
        var secret = await automation.RotateWebhookAsync(Actor, id, revoke, ct);
        return secret is null ? RedirectToAction(nameof(Index)) : View("Secret", new WebhookSecretModel(id, secret));
    });

    [HttpGet("webhooks/{id:guid}/deliveries")]
    public Task<IActionResult> Deliveries(Guid id, CancellationToken ct) => Mutate(async () =>
        View(new WebhookHistoryModel(id, await automation.DeliveriesAsync(Actor, id, ct))));

    [HttpGet("images/edit")]
    public async Task<IActionResult> ImageEditor(Guid? id, CancellationToken ct)
    {
        if (id is null) return View(new ImagePolicyEditorModel());
        var x = (await automation.ListAsync(Actor, ct)).ImagePolicies.SingleOrDefault(x => x.Id == id);
        return x is null ? Forbid() : View(new ImagePolicyEditorModel { Id = x.Id, Revision = x.Revision, Name = x.Name, DeploymentId = x.DeploymentId,
            Services = x.Services, Mode = x.Mode, Enabled = x.Enabled, FrequencyMinutes = x.FrequencyMinutes, FailureBackoffMinutes = x.FailureBackoffMinutes });
    }

    [HttpPost("images/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ImageEditor(ImagePolicyEditorModel model, CancellationToken ct)
    {
        try
        {
            await authorization.RequireAsync(Actor, "automation.manage", "deployment", model.DeploymentId, ct);
            if (!ModelState.IsValid) return View(model);
            await automation.SaveImagePolicyAsync(Actor, new() { Id = model.Id, Name = model.Name, DeploymentId = model.DeploymentId, Services = model.Services,
                Mode = model.Mode, Enabled = model.Enabled, FrequencyMinutes = model.FrequencyMinutes, FailureBackoffMinutes = model.FailureBackoffMinutes }, model.Revision, ct);
            return RedirectToAction(nameof(Index));
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e)) { ModelState.AddModelError("", Message(e)); return View(model); }
    }

    [HttpPost("images/{id:guid}/check"), ValidateAntiForgeryToken]
    public Task<IActionResult> CheckImages(Guid id, CancellationToken ct) => Mutate(async () =>
    { var run = await automation.CheckImagesAsync(Actor, id, ct); return RedirectToAction(nameof(Run), new { id = run.Id }); });

    [HttpGet("runs/{id:guid}")]
    public Task<IActionResult> Run(Guid id, CancellationToken ct) => Mutate(async () => View(await automation.ReadRunAsync(Actor, id, ct)));

    [HttpPost("runs/{id:guid}/cancel"), ValidateAntiForgeryToken]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Mutate(async () =>
    { await automation.CancelRunAsync(Actor, id, ct); return RedirectToAction(nameof(Run), new { id }); });

    [HttpGet("limits")]
    public Task<IActionResult> Limits(CancellationToken ct) => Mutate(async () =>
    { await authorization.RequireAsync(Actor, "settings.read", "instance", null, ct); return View(await automation.LimitsAsync(ct)); });

    [HttpPost("runs/{id:guid}/reconcile"), ValidateAntiForgeryToken]
    public Task<IActionResult> Reconcile(Guid id, bool confirmedStopped, CancellationToken ct) => Mutate(async () =>
    { await automation.ReconcileRunAsync(Actor, id, confirmedStopped, ct); return RedirectToAction(nameof(Run), new { id }); });

    [HttpPost("limits"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Limits(AutomationLimits model, CancellationToken ct)
    {
        try { await automation.SaveLimitsAsync(Actor, model, ct); return RedirectToAction(nameof(Index)); }
        catch (AutomationAccessException) { return Forbid(); }
        catch (AutomationException e) { ModelState.AddModelError("", e.Message); return View(model); }
    }

    private async Task<IActionResult> Mutate(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AutomationAccessException) { return Forbid(); }
        catch (Exception e) when (Safe(e)) { TempData["Error"] = Message(e); return RedirectToAction(nameof(Index)); }
    }
    private static bool Safe(Exception e) => e is AutomationException or DbUpdateException;
    private static string Message(Exception e) => e is AutomationException ? e.Message : "Automation could not be saved. Reload and check database availability.";
}
