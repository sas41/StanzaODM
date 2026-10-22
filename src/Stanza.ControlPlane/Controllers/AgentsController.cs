using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Agents;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("agents")]
public sealed class AgentsController(AgentAdministrationService agents) : Controller
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        try { return View(new AgentListModel(await agents.ListAsync(ActorId, token))); } catch (AgentOperationException) { return Forbid(); }
    }

    [HttpPost("{id:guid}/rotate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken token)
    {
        try { var value = await agents.RotateCredentialAsync(ActorId, id, token); TempData["EnrollmentToken"] = value.Token; TempData["EnrollmentExpires"] = DateTimeOffset.FromUnixTimeSeconds(value.ExpiresAt).ToString("u"); TempData["Success"] = "Replace the Agent bootstrap credential and restart it. Registration requires approval again; old credentials are revoked."; }
        catch (AgentOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("tokens"), ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueToken(int lifetimeMinutes = 15, CancellationToken token = default)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Choose a valid token lifetime."; return RedirectToAction(nameof(Index)); }
        try
        {
            var issued = await agents.IssueTokenAsync(ActorId, lifetimeMinutes, token);
            TempData["EnrollmentToken"] = issued.Token;
            TempData["EnrollmentExpires"] = DateTimeOffset.FromUnixTimeSeconds(issued.ExpiresAt).ToString("u");
        }
        catch (AgentOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(Guid id, string status, CancellationToken token)
    {
        try { await agents.SetStatusAsync(ActorId, id, status, token); TempData["Success"] = "Agent registration updated."; }
        catch (AgentOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/environment")]
    public async Task<IActionResult> Environment(Guid id, CancellationToken token)
    {
        try
        {
            var environment = await agents.ReadEnvironmentAsync(ActorId, id, token);
            return View(new AgentEnvironmentModel { Id = id, Text = environment.Text, Version = environment.Version });
        }
        catch (AgentOperationException error) { return StatusCode(error.StatusCode); }
    }

    [HttpPost("{id:guid}/environment"), ValidateAntiForgeryToken, RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Environment(Guid id, AgentEnvironmentModel model, CancellationToken token)
    {
        model.Id = id;
        if (!ModelState.IsValid) return View(model);
        try
        {
            await agents.SaveEnvironmentAsync(ActorId, id, new(model.Text ?? "", model.Version), token);
            TempData["Success"] = "Agent environment saved. Workloads were not restarted.";
            return RedirectToAction(nameof(Environment), new { id });
        }
        catch (AgentOperationException error) { ModelState.AddModelError("", error.Message); return View(model); }
    }
}
