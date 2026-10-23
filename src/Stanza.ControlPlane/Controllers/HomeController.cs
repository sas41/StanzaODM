using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Models;

namespace Stanza.ControlPlane.Controllers;

public sealed class HomeController : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> Index([FromServices] Automation.DeploymentActionOverview overview, CancellationToken ct) =>
        View(await overview.ReadAsync(Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value), ct));

    [HttpGet("/overview/deployments")]
    public async Task<IActionResult> Deployments([FromServices] Automation.DeploymentActionOverview overview, CancellationToken ct) =>
        PartialView("_Deployments", await overview.ReadAsync(Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value), ct));

    [HttpGet("/status")]
    public async Task<IActionResult> Status([FromServices] StanzaDbContext store, [FromServices] AgentAdministrationService agents, CancellationToken token)
    {
        var ready = await store.Database.CanConnectAsync(token);
        int? count = null;
        int? pending = null;
        if (User.IsInRole("Admin") || User.IsInRole("SystemAdmin"))
        {
            var actorId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
            var registrations = await agents.ListAsync(actorId, token);
            count = registrations.Count;
            pending = registrations.Count(x => x.Status == "pending");
        }
        return PartialView("_Status", new SystemStatusModel(ready, count, pending));
    }

    [HttpGet("/error"), HttpPost("/error"), AllowAnonymous, IgnoreAntiforgeryToken]
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View("Error");
    }
}
