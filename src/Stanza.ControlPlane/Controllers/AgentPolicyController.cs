using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Configuration;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("agents/{id:guid}/policy")]
public sealed class AgentPolicyController(AgentPolicyService policies) : Controller
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("")] public async Task<IActionResult> Index(Guid id, CancellationToken ct)
    { try { return View(await policies.ReadAsync(Actor, id, ct)); } catch (SettingsOperationException) { return Forbid(); } }
    [HttpPost(""), ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(Guid id, bool executionEnabled, string dockerEndpoint, long version, CancellationToken ct)
    {
        var edit = new AgentPolicyEditor(id, new(executionEnabled, DockerEndpoint: dockerEndpoint), version);
        if (!ModelState.IsValid) return View(edit);
        try { await policies.SaveAsync(Actor, edit, ct); TempData["Success"] = "Agent runtime policy saved for future commands."; return RedirectToAction(nameof(Index), new { id }); }
        catch (SettingsOperationException error) { ModelState.AddModelError("", error.Message); return View(edit); }
    }
}
