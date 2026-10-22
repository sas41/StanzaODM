using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;

namespace Stanza.ControlPlane.Controllers;

[ApiController, AllowAnonymous, Route("api/agent-enrollment")]
public sealed class AgentEnrollmentController(AgentAdministrationService agents) : ControllerBase
{
    [HttpPost, RequestSizeLimit(16 * 1024), EnableRateLimiting("enrollment")]
    public async Task<IActionResult> Register(AgentEnrollmentRequest request, CancellationToken token)
    {
        try
        {
            var result = await agents.RegisterAsync(request, token);
            return StatusCode(result.Status == AgentEnrollmentProtocol.Pending ? 202 : 200, result);
        }
        catch (AgentOperationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }
}
