using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Sources;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("sources"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(65536)]
public sealed class SourcesController(SourceService sources) : Controller
{
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await sources.ListDeploymentsAsync(Actor, ct));

    [HttpGet("{deploymentId:guid}")]
    public async Task<IActionResult> Details(Guid deploymentId, CancellationToken ct)
    {
        try { return View(new SourceDetailsModel(await sources.ReadAsync(Actor, deploymentId, ct), await sources.UsableCredentialsAsync(Actor, ct))); }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { TempData["Error"] = e.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpGet("{deploymentId:guid}/edit")]
    public async Task<IActionResult> Edit(Guid deploymentId, Guid? id, CancellationToken ct)
    {
        try
        {
            var deployment = await sources.ReadAsync(Actor, deploymentId, ct);
            if (!deployment.CanManage) return Forbid();
            var row = id.HasValue ? deployment.Git.SingleOrDefault(x => x.Id == id) ?? throw new SourceException("That source is unavailable.") : null;
            return View(new GitSourceEditModel { DeploymentId = deploymentId, Id = row?.Id, ExpectedVersion = row?.Version ?? 0,
                Url = row?.Url ?? "", Reference = row?.Reference ?? "main", Destination = row?.Destination ?? "sources/app", CredentialId = row?.CredentialId,
                Credentials = (await sources.UsableCredentialsAsync(Actor, ct)).Where(x => x.Kind == "git").ToArray() });
        }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { TempData["Error"] = e.Message; return RedirectToAction(nameof(Details), new { deploymentId }); }
    }

    [HttpPost("{deploymentId:guid}/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid deploymentId, GitSourceEditModel model, CancellationToken ct)
    {
        model.DeploymentId = deploymentId;
        try
        {
            if (!ModelState.IsValid) throw new SourceException("Check the submitted source fields.");
            await sources.SaveGitAsync(Actor, deploymentId, model.Id, model.ExpectedVersion, new(model.Url, model.Reference, model.Destination, model.CredentialId), ct);
            TempData["Success"] = "Git source saved. Compose was not modified. Refresh references below to select a branch or tag.";
            return RedirectToAction(nameof(Details), new { deploymentId });
        }
        catch (SourceAccessException) { return Forbid(); }
        catch (Exception e) when (e is SourceException or DbUpdateException)
        {
            if (!(await sources.ReadAsync(Actor, deploymentId, ct)).CanManage) return Forbid();
            model.Credentials = (await sources.UsableCredentialsAsync(Actor, ct)).Where(x => x.Kind == "git").ToArray();
            ModelState.AddModelError("", e is SourceException ? e.Message : "The source could not be saved. Reload and retry.");
            return View("Edit", model);
        }
    }

    [HttpPost("{deploymentId:guid}/delete"), ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid deploymentId, Guid id, long expectedVersion, CancellationToken ct) =>
        Mutate(deploymentId, () => sources.DeleteGitAsync(Actor, deploymentId, id, expectedVersion, ct));

    [HttpPost("{deploymentId:guid}/reorder"), ValidateAntiForgeryToken]
    public Task<IActionResult> Reorder(Guid deploymentId, Guid[] ids, long[] versions, CancellationToken ct) =>
        Mutate(deploymentId, () => sources.ReorderAsync(Actor, deploymentId, ids, versions, ct));

    [HttpPost("{deploymentId:guid}/registries"), ValidateAntiForgeryToken]
    public Task<IActionResult> Registries(Guid deploymentId, Guid[] credentialIds, Guid[] expectedCredentialIds, CancellationToken ct) =>
        Mutate(deploymentId, () => sources.SetRegistriesAsync(Actor, deploymentId, credentialIds, expectedCredentialIds, ct));

    [HttpGet("{deploymentId:guid}/{sourceId:guid}/branches")]
    public async Task<IActionResult> Branches(Guid deploymentId, Guid sourceId, string? search, int page = 0, CancellationToken ct = default)
    {
        try { return PartialView("_Branches", new BranchPartialModel(deploymentId, sourceId, await sources.BranchesAsync(Actor, deploymentId, sourceId, search, page, ct))); }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { return PartialView("_Branches", new BranchPartialModel(deploymentId, sourceId, null, e.Message)); }
    }

    private async Task<IActionResult> Mutate(Guid deploymentId, Func<Task> action)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Invalid source configuration."; return RedirectToAction(nameof(Details), new { deploymentId }); }
        try { await action(); TempData["Success"] = "Source configuration saved. Compose was not modified."; }
        catch (SourceAccessException) { return Forbid(); }
        catch (Exception e) when (e is SourceException or DbUpdateException)
        { TempData["Error"] = e is SourceException ? e.Message : "The configuration could not be saved. Reload and retry."; }
        return RedirectToAction(nameof(Details), new { deploymentId });
    }
}
