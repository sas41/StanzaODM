using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Controllers;

[ApiController, AllowAnonymous, Route("api/agent-enrollment")]
public sealed class AgentEnrollmentController(AgentAdministrationService agents, IHostEnvironment environment, HttpTransportPolicy? httpTransport = null) : ControllerBase
{
    [HttpPost, RequestSizeLimit(16 * 1024), EnableRateLimiting("enrollment")]
    public async Task<IActionResult> Register(AgentEnrollmentRequest request, CancellationToken token)
    {
        if (!(httpTransport ?? new()).AllowsAgentRequest(HttpContext, environment))
            return BadRequest(new { error = "HTTPS is required for Agent enrollment unless STANZA_ALLOW_INSECURE_HTTP is explicitly enabled on both deployments." });
        try
        {
            var result = await agents.RegisterAsync(request, token);
            return StatusCode(result.Status == AgentEnrollmentProtocol.Pending ? 202 : 200, result);
        }
        catch (AgentOperationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }
}
