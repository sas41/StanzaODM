using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Recovery;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Controllers;

[Route("recovery")]
public sealed class RecoveryController(RecoveryService recovery, InstanceMaintenance? maintenance = null, IHostApplicationLifetime? lifetime = null) : Controller
{
    private Guid ActorId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet(""), AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var fresh = await recovery.CanRestoreAsync(token);
        if (!fresh && !await recovery.CanBackupAsync(ActorId, token)) return Forbid();
        return View(new RecoveryModel { FreshInstance = fresh });
    }

    [HttpPost("backup"), Authorize(Roles = "SystemAdmin"), ValidateAntiForgeryToken, EnableRateLimiting("authentication"), RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Backup(RecoveryModel model, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await recovery.CanBackupAsync(ActorId, token)) return Forbid();
        model.FreshInstance = false; model.Restored = false;
        if (!model.Acknowledge) ModelState.AddModelError("", "Acknowledge the complete-instance secret and recovery warnings before continuing.");
        if (!ModelState.IsValid) return CleanView(model);
        try
        {
            var bytes = await recovery.BackupAsync(ActorId, model.Passphrase, token);
            return File(bytes, "application/octet-stream", $"stanza-instance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.stanza-recovery");
        }
        catch (RecoveryOperationException error) { ModelState.AddModelError("", error.Message); return CleanView(model); }
    }

    [HttpPost("restore"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting("authentication")]
    [RequestSizeLimit(RecoveryService.MaximumArchiveBytes + 64 * 1024), RequestFormLimits(MultipartBodyLengthLimit = RecoveryService.MaximumArchiveBytes + 32 * 1024)]
    public async Task<IActionResult> Restore(RecoveryModel model, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await recovery.CanRestoreAsync(token)) return Forbid();
        model.FreshInstance = true; model.Restored = false;
        if (!model.Acknowledge) ModelState.AddModelError("", "Acknowledge the restart and complete-instance recovery warnings before continuing.");
        if (model.Archive is null || model.Archive.Length is < 1 or > RecoveryService.MaximumArchiveBytes)
            ModelState.AddModelError("", "Select an encrypted instance recovery archive no larger than 128 MiB.");
        if (!ModelState.IsValid) return CleanView(model);
        try
        {
            using var stream = new BoundedRecoveryStream(RecoveryService.MaximumArchiveBytes);
            await model.Archive!.CopyToAsync(stream, token);
            await recovery.RestoreAsync(stream.ToArray(), model.Passphrase, token);
            if (maintenance?.RestartRequired == true) ViewData["RestartTicket"] = maintenance.IssueRestartTicket();
            return View("Index", new RecoveryModel { Restored = true });
        }
        catch (RecoveryOperationException error) { ModelState.AddModelError("", error.Message); return CleanView(model); }
    }

    [HttpPost("restart"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting("authentication"), RequestSizeLimit(16 * 1024)]
    public IActionResult Restart(string ticket)
    {
        if (maintenance is null || lifetime is null || !maintenance.ConsumeRestartTicket(ticket)) return Forbid();
        Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
        return Content("Restart requested. Docker's restart policy will start the Control Plane again. Sign in after it is ready.");
    }

    private IActionResult CleanView(RecoveryModel model)
    {
        // Never echo a submitted passphrase through validation helpers or redisplay uploads.
        model.Passphrase = ""; model.Archive = null;
        if (ModelState.TryGetValue(nameof(model.Passphrase), out var entry))
            foreach (var error in entry.Errors) ModelState.AddModelError("", error.ErrorMessage);
        ModelState.Remove(nameof(model.Passphrase));
        return View("Index", model);
    }
}
