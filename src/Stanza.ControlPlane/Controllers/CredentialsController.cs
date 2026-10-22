using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Sources;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("credentials"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(65536)]
public sealed class CredentialsController(SourceService sources) : Controller
{
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new CredentialListModel(
        await sources.ListCredentialsAsync(Actor, ct), await sources.ListAgentsAsync(Actor, ct), await sources.CanWriteCredentialAsync(Actor, null, ct)));

    [HttpGet("create")]
    public async Task<IActionResult> Create(CancellationToken ct) => await sources.CanWriteCredentialAsync(Actor, null, ct)
        ? View("Edit", new CredentialEditModel()) : Forbid();

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        try
        {
            if (!await sources.CanWriteCredentialAsync(Actor, id, ct)) return Forbid();
            var row = await sources.ReadCredentialAsync(Actor, id, ct);
            return View(new CredentialEditModel { Id = id, Name = row.Name, Kind = row.Kind, Endpoint = row.Endpoint, ExpectedVersion = row.Version });
        }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { TempData["Error"] = e.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpPost("save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CredentialEditModel model, CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid) throw new SourceException("Check the submitted credential fields.");
            await sources.SaveCredentialAsync(Actor, model.Id, model.ExpectedVersion,
                new(model.Name, model.Kind, model.Endpoint, model.Username, model.Password), ct);
            TempData["Success"] = "Credential encrypted and saved.";
            return RedirectToAction(nameof(Index));
        }
        catch (SourceAccessException) { return Forbid(); }
        catch (Exception e) when (e is SourceException or DbUpdateException)
        {
            if (!await sources.CanWriteCredentialAsync(Actor, model.Id, ct)) return Forbid();
            ModelState.Clear(); model.Username = ""; model.Password = "";
            ModelState.AddModelError("", e is SourceException ? e.Message : "The credential could not be saved. Reload and retry.");
            return View("Edit", model);
        }
    }

    [HttpPost("{id:guid}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, long expectedVersion, CancellationToken ct)
    {
        try { await sources.DeleteCredentialAsync(Actor, id, expectedVersion, ct); TempData["Success"] = "Credential deleted."; }
        catch (SourceAccessException) { return Forbid(); }
        catch (Exception e) when (e is SourceException or DbUpdateException)
        { TempData["Error"] = e is SourceException ? e.Message : "Deletion failed. Remove references and reload before retrying."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/reveal"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reveal(Guid id, CancellationToken ct)
    {
        try { return View("Reveal", await sources.RevealAsync(Actor, id, ct)); }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { TempData["Error"] = e.Message; return RedirectToAction(nameof(Index)); }
    }

    [HttpPost("{id:guid}/test"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid id, Guid targetAgentId, CancellationToken ct)
    {
        try { await sources.TestCredentialAsync(Actor, id, targetAgentId, ct); TempData["Success"] = "The selected Agent successfully authenticated to the endpoint."; }
        catch (SourceAccessException) { return Forbid(); }
        catch (SourceException e) { TempData["Error"] = e.Message; }
        return RedirectToAction(nameof(Index));
    }
}
