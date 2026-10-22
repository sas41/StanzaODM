using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Models;
using Stanza.Core.Security;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[Authorize(Roles = "Admin,SystemAdmin"), Route("users")]
public sealed class UsersController(AccountService accounts, AccessService access) : Controller
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token) => View(await accounts.ListAsync(token));

    [HttpGet("create")]
    public IActionResult Create() => View(new CreateUserModel());

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserModel model, CancellationToken token)
    {
        if (!Enum.IsDefined(model.Role)) ModelState.AddModelError(nameof(model.Role), "Choose a valid account type.");
        if (!ModelState.IsValid) return View(model);
        try
        {
            await accounts.CreateAsync(ActorId, model.Username, model.Password, model.Role, token);
            TempData["Success"] = "Account created.";
            return RedirectToAction(nameof(Index));
        }
        catch (SecurityOperationException error)
        {
            ModelState.AddModelError("", error.Message);
            return View(model);
        }
    }

    [HttpPost("{id:guid}/enabled"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Enabled(Guid id, bool? enabled, CancellationToken token)
    {
        if (!ModelState.IsValid || enabled is null) return BadRequest("Choose an explicit account status.");
        try { await accounts.SetEnabledAsync(ActorId, id, enabled.Value, token); TempData["Success"] = "Account status updated."; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/role"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Role(Guid id, AccountRole? role, CancellationToken token)
    {
        if (!ModelState.IsValid || role is null || !Enum.IsDefined(role.Value)) return BadRequest("Choose a valid account type.");
        try { await accounts.SetRoleAsync(ActorId, id, role.Value, token); TempData["Success"] = "Account type updated."; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid id, string password, CancellationToken token)
    {
        try { await accounts.ResetPasswordAsync(ActorId, id, password, token); TempData["Success"] = "Password reset and sessions revoked."; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken token)
    {
        try { await accounts.DeleteAsync(ActorId, id, token); TempData["Success"] = "Account deleted."; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/access")]
    public async Task<IActionResult> Access(Guid id, CancellationToken token)
    {
        var account = await accounts.FindAsync(id, token);
        return account is null ? NotFound() : View(new AccountAccessModel(account, await access.GetAccessAsync(id, token)));
    }

    [HttpPost("{id:guid}/grants"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddGrant(Guid id, string permission, Guid? resourceId, CancellationToken token)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Invalid permission or resource ID."; return RedirectToAction(nameof(Access), new { id }); }
        try
        {
            var (action, resourceType) = GroupsController.ParsePermission(permission);
            await access.AddGrantAsync(ActorId, id, null, action, resourceType, resourceId, token);
            TempData["Success"] = "Permission granted.";
        }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Access), new { id });
    }

    [HttpPost("{id:guid}/grants/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveGrant(Guid id, Guid grantId, CancellationToken token)
    {
        if (await accounts.FindAsync(id, token) is null) return NotFound();
        var current = await access.GetAccessAsync(id, token);
        if (!current.DirectGrants.Any(x => x.Id == grantId)) return NotFound();
        try { await access.RemoveGrantAsync(ActorId, grantId, token); TempData["Success"] = "Permission removed."; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Access), new { id });
    }
}
