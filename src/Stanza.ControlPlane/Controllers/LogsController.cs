using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Logs;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("logs"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LogsController(LogAccess access) : Controller
{
    [HttpGet("{deploymentId:guid}")]
    public async Task<IActionResult> Index(Guid deploymentId, CancellationToken ct)
    {
        try { return View(await access.RequireAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), deploymentId, ct)); }
        catch (LogAccessException error)
        {
            if (error.StatusCode == 403) return Forbid();
            Response.StatusCode = error.StatusCode;
            ViewData["Error"] = error.Message;
            return View("Unavailable");
        }
    }
}
