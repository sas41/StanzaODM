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
        try { await policies.ReadAsync(Actor, ct); return Redirect("/settings#operational-settings"); } catch (SettingsOperationException) { return Forbid(); }
    }
    [HttpPost(""), ValidateAntiForgeryToken, Authorize(Roles = "Admin,SystemAdmin")]
    public IActionResult Save() => RedirectPreserveMethod("/settings/operations");
}
