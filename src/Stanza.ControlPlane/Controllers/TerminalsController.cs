using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Terminals;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("terminals"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TerminalsController(TerminalAccess access) : Controller
{
    [HttpGet("{deploymentId:guid}")]
    public async Task<IActionResult> Index(Guid deploymentId, CancellationToken ct)
    {
        try { return View(await access.RequireAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), deploymentId, ct)); }
        catch (TerminalAccessException error)
        {
            if (error.StatusCode == 403) return Forbid();
            Response.StatusCode = error.StatusCode;
            ViewData["Error"] = error.Message;
            return View("Unavailable");
        }
    }
}
