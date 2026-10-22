using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("policy")]
public sealed class PolicyController(OperationalPolicyService policies) : Controller
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try { return View(await policies.ReadAsync(Actor, ct)); } catch (SettingsOperationException) { return Forbid(); }
    }
    [HttpPost(""), ValidateAntiForgeryToken, Authorize(Roles = "Admin,SystemAdmin")]
    public async Task<IActionResult> Index(OperationalPolicy policy, long version, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(new PolicyEditor(policy, version));
        try { await policies.SaveAsync(Actor, new(policy, version), ct); TempData["Success"] = "Policy saved. Existing sessions retain their original expiry; new sessions and operations use the new limits."; return RedirectToAction(nameof(Index)); }
        catch (SettingsOperationException error) { ModelState.AddModelError("", error.Message); return View(new PolicyEditor(policy, version)); }
    }
}
