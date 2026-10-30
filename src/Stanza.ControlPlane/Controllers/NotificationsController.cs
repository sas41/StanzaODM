using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Automation;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("settings/notifications"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(OutgoingWebhookService service) : Controller
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try { return View(await service.ReadAsync(Actor, ct)); }
        catch (AutomationAccessException) { return Forbid(); }
    }
    [HttpPost(""), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(OutgoingEditor input, CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid) { TempData["Error"] = "Invalid channel settings."; return RedirectToAction(nameof(Index)); }
            await service.SaveAsync(Actor, input, ct); TempData["Success"] = "Channel saved. Existing endpoint secrets are preserved when the URL is left blank.";
        }
        catch (AutomationAccessException) { return Forbid(); }
        catch (AutomationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("{id:guid}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, long revision, CancellationToken ct)
    {
        try { await service.DeleteAsync(Actor, id, revision, ct); }
        catch (AutomationAccessException) { return Forbid(); }
        catch (AutomationException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Index));
    }
}
