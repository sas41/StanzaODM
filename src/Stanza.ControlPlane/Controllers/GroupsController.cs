using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[Authorize(Roles = "Admin,SystemAdmin"), Route("groups")]
public sealed class GroupsController(AccessService access, AccountService accounts) : Controller
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token) =>
        View(new GroupsModel(await access.ListGroupsAsync(token), await accounts.ListAsync(token)));

    [HttpPost("create"), ValidateAntiForgeryToken]
    public Task<IActionResult> Create(string name, CancellationToken token) =>
        MutateAsync(async () => { await access.CreateGroupAsync(ActorId, name, token); }, "Group created.");

    [HttpPost("{id:guid}/rename"), ValidateAntiForgeryToken]
    public Task<IActionResult> Rename(Guid id, string name, CancellationToken token) =>
        MutateAsync(() => access.RenameGroupAsync(ActorId, id, name, token), "Group renamed.");

    [HttpPost("{id:guid}/delete"), ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid id, CancellationToken token) =>
        MutateAsync(() => access.DeleteGroupAsync(ActorId, id, token), "Group deleted.");

    [HttpPost("{id:guid}/members/add"), ValidateAntiForgeryToken]
    public Task<IActionResult> AddMember(Guid id, Guid accountId, CancellationToken token) =>
        MutateAsync(() => access.AddMemberAsync(ActorId, id, accountId, token), "Group member added.");

    [HttpPost("{id:guid}/members/remove"), ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveMember(Guid id, Guid accountId, CancellationToken token) =>
        MutateAsync(() => access.RemoveMemberAsync(ActorId, id, accountId, token), "Group member removed.");

    [HttpPost("{id:guid}/grants"), ValidateAntiForgeryToken]
    public Task<IActionResult> AddGrant(Guid id, string permission, Guid? resourceId, CancellationToken token) =>
        MutateAsync(async () =>
        {
            var (action, resourceType) = ParsePermission(permission);
            await access.AddGrantAsync(ActorId, null, id, action, resourceType, resourceId, token);
        }, "Group permission granted.");

    [HttpPost("grants/{id:guid}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveGrant(Guid id, CancellationToken token)
    {
        var groups = await access.ListGroupsAsync(token);
        if (!groups.Any(group => group.Grants.Any(grant => grant.Id == id))) return NotFound();
        return await MutateAsync(() => access.RemoveGrantAsync(ActorId, id, token), "Permission removed.");
    }

    private async Task<IActionResult> MutateAsync(Func<Task> mutation, string message)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Invalid form values."; return RedirectToAction(nameof(Index)); }
        try { await mutation(); TempData["Success"] = message; }
        catch (SecurityOperationException error) { TempData["Error"] = error.Message; }
        return RedirectToAction(nameof(Index));
    }

    internal static (string Action, string ResourceType) ParsePermission(string? permission)
    {
        var parts = permission?.Split('|');
        if (parts is not { Length: 2 }) throw new SecurityOperationException("Choose an available permission.");
        return (parts[0], parts[1]);
    }
}
