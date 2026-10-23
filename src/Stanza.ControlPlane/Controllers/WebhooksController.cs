using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Controllers;

[AllowAnonymous, Route("hooks"), IgnoreAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[DisableRequestSizeLimit]
public sealed class WebhooksController(WebhookReceiver receiver, AutomationService automation, StanzaDbContext db) : ControllerBase
{
    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Receive(Guid id, CancellationToken ct)
    {
        var limit = (await automation.LimitsAsync(ct)).MaximumWebhookBytes;
        if (limit > 0 && Request.ContentLength > limit) return StatusCode(413, new { code = "payload-too-large" });
        var hook = await db.Set<WebhookDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (hook is null || !hook.Enabled) return NotFound();
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (Stanza.Core.Contracts.LimitPolicy.Exceeded(body.Length + read, limit)) return StatusCode(413, new { code = "payload-too-large" });
            body.Write(buffer, 0, read);
        }
        var github = hook.Provider == "github";
        var signature = Request.Headers[github ? "X-Hub-Signature-256" : "X-Stanza-Signature-256"].ToString();
        var delivery = Request.Headers[github ? "X-GitHub-Delivery" : "X-Stanza-Delivery"].ToString();
        var eventName = Request.Headers[github ? "X-GitHub-Event" : "X-Stanza-Event"].ToString();
        try
        {
            var receipt = await receiver.ReceiveAsync(id, signature, delivery, eventName, body.ToArray(), ct);
            return StatusCode(receipt.Status, new { code = receipt.Code, runId = receipt.RunId });
        }
        catch (DbUpdateException) { return StatusCode(409, new { code = "retry-delivery" }); }
    }
}
