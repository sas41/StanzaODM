using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("settings")]
public sealed class SettingsController(InstanceSettingsService settings, OperationalPolicyService policies) : Controller
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        InstanceSettings current;
        try { current = await settings.ReadAsync(ActorId, token); } catch (SettingsOperationException) { return Forbid(); }
        Response.Headers.CacheControl = "no-store";
        return View(new SettingsModel { PublicUrl = current.PublicUrl, GlobalEnvironment = current.GlobalEnvironment, Version = current.Version,
            CanWriteSettings = await settings.CanAsync(ActorId, "settings.write", token), CanRevealEnvironment = await settings.CanAsync(ActorId, "environment.reveal", token), CanWriteEnvironment = await settings.CanAsync(ActorId, "environment.write", token), Operational = await ReadPolicyAsync(token) });
    }

    [HttpPost(""), ValidateAntiForgeryToken, RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Index(SettingsModel model, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        model.CanWriteSettings = await settings.CanAsync(ActorId, "settings.write", token);
        model.CanRevealEnvironment = await settings.CanAsync(ActorId, "environment.reveal", token);
        model.CanWriteEnvironment = await settings.CanAsync(ActorId, "environment.write", token);
        model.Operational = await ReadPolicyAsync(token);
        if (!model.CanWriteSettings && !model.CanWriteEnvironment) return Forbid();
        if (!ModelState.IsValid) return View(model);
        try
        {
            await settings.SaveEditorAsync(ActorId, model.CanWriteSettings ? model.PublicUrl ?? "" : null,
                model.CanWriteEnvironment && (model.CanRevealEnvironment || model.ReplaceEnvironment) ? model.GlobalEnvironment ?? "" : null, model.Version, token);
            TempData["Success"] = "Settings saved. Existing workloads were not restarted.";
            return RedirectToAction(nameof(Index));
        }
        catch (SettingsOperationException error)
        {
            ModelState.AddModelError("", error.Message);
            return View(model);
        }
    }

    private async Task<PolicyEditor?> ReadPolicyAsync(CancellationToken ct) => await settings.CanAsync(ActorId, "settings.read", ct) ? await policies.ReadAsync(ActorId, ct) : null;

    [HttpPost("operations"), ValidateAntiForgeryToken, Authorize(Roles = "Admin,SystemAdmin")]
    public async Task<IActionResult> Operations(OperationalPolicy policy, long version, CancellationToken token)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await policies.SaveAsync(ActorId, new(policy, version), token);
                TempData["Success"] = "Operational settings saved. Existing data and running workloads were not changed.";
                return Redirect("/settings#operational-settings");
            }
            catch (SettingsOperationException error) { ModelState.AddModelError("", error.Message); }
        }
        var result = await Index(token);
        if (result is ViewResult view && view.Model is SettingsModel model)
        {
            model.Operational = new(policy, version);
            view.ViewName = "Index";
        }
        return result;
    }
}
