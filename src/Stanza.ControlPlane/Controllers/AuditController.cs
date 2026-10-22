using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Controllers;

[Authorize, Route("audit")]
public sealed class AuditController(AuditService audit) : Controller
{
    [HttpGet("")] public async Task<IActionResult> Index(string? search, CancellationToken ct)
    {
        try { return View(await audit.ListAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), search, ct)); }
        catch (SecurityOperationException) { return Forbid(); }
    }
}
