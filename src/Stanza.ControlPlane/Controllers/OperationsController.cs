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
    [HttpGet("{id:guid}/output")]
    public async Task<IActionResult> Output(Guid id, [FromServices] OperationOutputStore output, CancellationToken ct)
    {
        if (!await operations.CanReadOutputAsync(Actor, id, ct)) return Forbid();
        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=operation-{id:N}.log";
        long cursor = 0;
        while (true)
        {
            // Long downloads must respect revocation too.
            if (!await operations.CanReadOutputAsync(Actor, id, ct)) break;
            var chunks = await output.ReadAsync(id, cursor, ct: ct);
            if (chunks.Count == 0) break;
            foreach (var chunk in chunks) await Response.WriteAsync(chunk.Text, ct);
            cursor = chunks[^1].Sequence;
        }
        if (cursor == 0) await Response.WriteAsync((await operations.DetailAsync(Actor, id, ct)).Output ?? "No retained output is available.", ct);
        return new EmptyResult();
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
