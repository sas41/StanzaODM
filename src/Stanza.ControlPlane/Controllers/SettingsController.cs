using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("settings")]
public sealed class SettingsController(InstanceSettingsService settings) : Controller
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        InstanceSettings current;
        try { current = await settings.ReadAsync(ActorId, token); } catch (SettingsOperationException) { return Forbid(); }
        Response.Headers.CacheControl = "no-store";
        return View(new SettingsModel { PublicUrl = current.PublicUrl, GlobalEnvironment = current.GlobalEnvironment, Version = current.Version,
            CanWriteSettings = await settings.CanAsync(ActorId, "settings.write", token), CanRevealEnvironment = await settings.CanAsync(ActorId, "environment.reveal", token), CanWriteEnvironment = await settings.CanAsync(ActorId, "environment.write", token) });
    }

    [HttpPost(""), ValidateAntiForgeryToken, RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Index(SettingsModel model, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        model.CanWriteSettings = await settings.CanAsync(ActorId, "settings.write", token);
        model.CanRevealEnvironment = await settings.CanAsync(ActorId, "environment.reveal", token);
        model.CanWriteEnvironment = await settings.CanAsync(ActorId, "environment.write", token);
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
}
