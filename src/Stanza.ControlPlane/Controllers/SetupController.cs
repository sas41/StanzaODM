using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[AllowAnonymous, Route("setup")]
public sealed class SetupController(AccountService accounts) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token) =>
        await accounts.NeedsSetupAsync(token) ? View(new SetupModel()) : RedirectToAction("Login", "Account");

    [HttpPost(""), ValidateAntiForgeryToken, EnableRateLimiting("authentication")]
    public async Task<IActionResult> Index(SetupModel model, CancellationToken token)
    {
        if (!await accounts.NeedsSetupAsync(token)) return RedirectToAction("Login", "Account");
        if (!ModelState.IsValid) return View(model);
        try
        {
            await accounts.BootstrapAsync(model.Username, model.Password, token);
            TempData["Success"] = "System Admin created. Sign in to continue.";
            return RedirectToAction("Login", "Account");
        }
        catch (SecurityOperationException error)
        {
            ModelState.AddModelError("", error.Message);
            return View(model);
        }
    }
}
