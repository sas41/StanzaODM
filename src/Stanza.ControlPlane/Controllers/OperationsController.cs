using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Operations;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("operations"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OperationsController(OperationService operations) : Controller
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("")] public async Task<IActionResult> Index(Guid? deploymentId, CancellationToken ct) => View(await operations.ListAsync(Actor, deploymentId, ct));
    [HttpGet("{id:guid}")] public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        try { return View(await operations.DetailAsync(Actor, id, ct)); } catch (OperationException) { return Forbid(); }
    }
    [HttpPost("queue"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Queue(Guid deploymentId, string kind, CancellationToken ct)
    {
        try { var op = await operations.EnqueueAsync(new(Actor, deploymentId, kind), ct); return RedirectToAction(nameof(Details), new { id = op.Id }); }
        catch (Exception error) when (error is OperationException or Deployments.DeploymentDraftException) { TempData["Error"] = error.Message; return RedirectToAction(nameof(Index)); }
    }
    [HttpPost("{id:guid}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        try { await operations.CancelAsync(Actor, id, ct); } catch (OperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpPost("{id:guid}/reconcile"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(Guid id, bool acknowledged, CancellationToken ct)
    {
        if (!acknowledged) return BadRequest();
        try { await operations.ReconcileAsync(Actor, id, ct); } catch (OperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }
}
