using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[Route("account")]
public sealed class AccountController(AccountService accounts) : Controller
{
    [HttpGet("login"), AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken token)
    {
        if (await accounts.NeedsSetupAsync(token)) return RedirectToAction("Index", "Setup");
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : "/" });
    }

    [HttpPost("login"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting("authentication")]
    public async Task<IActionResult> Login(LoginModel model, CancellationToken token)
    {
        if (!ModelState.IsValid) return View(model);
        var account = await accounts.AuthenticateAsync(model.Username, model.Password, token);
        if (account is null)
        {
            ModelState.AddModelError("", "Invalid username or password.");
            ModelState.Remove(nameof(model.Password));
            model.Password = "";
            return View(model);
        }
        await SignInAsync(account.Id, account.Username, account.Role.ToString(), account.SecurityStamp);
        return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : "/");
    }

    [HttpPost("logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("password")]
    public IActionResult Password() => View(new ChangePasswordModel());

    [HttpPost("password"), ValidateAntiForgeryToken, EnableRateLimiting("authentication")]
    public async Task<IActionResult> Password(ChangePasswordModel model, CancellationToken token)
    {
        if (!ModelState.IsValid) return View(model);
        try
        {
            await accounts.ChangePasswordAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), model.CurrentPassword, model.NewPassword, token);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["Success"] = "Password changed. Sign in again with your new password.";
            return RedirectToAction(nameof(Login));
        }
        catch (SecurityOperationException error)
        {
            ModelState.AddModelError("", error.Message);
            return View(new ChangePasswordModel());
        }
    }

    [HttpGet("denied")]
    public IActionResult Denied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    private Task SignInAsync(Guid id, string username, string role, string stamp) =>
        HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, id.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                new Claim("security_stamp", stamp)
            ], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
}
