using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.Core.Security;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Live;

public static class LiveStatusExtensions
{
    public static IServiceCollection AddStanzaLiveStatus(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LiveConnectionLeases>();
        return services;
    }

    public static IEndpointRouteBuilder MapStanzaLiveStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/live/status", HandleAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var publicOrigin = await context.RequestServices.GetRequiredService<InstanceSettingsService>().ReadPublicOriginAsync(context.RequestAborted);
        if (!IsSameOrigin(context.Request, publicOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            || context.User.FindFirstValue("security_stamp") is not { Length: > 0 } stamp
            || authentication.Properties?.ExpiresUtc is not { } expires)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var policy = (await context.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.RequestAborted)).Policy;
        using var lease = context.RequestServices.GetRequiredService<LiveConnectionLeases>().TryAcquire(actorId, policy.MaximumConnectionsPerUser, policy.MaximumConnections);
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var feed = SendStatusAsync(socket, scopes, actorId, stamp, expires, clock, cancellation.Token);
        var input = RejectInputAsync(socket, cancellation.Token);
        var close = WebSocketCloseStatus.InternalServerError;
        try
        {
            var completed = await Task.WhenAny(feed, input);
            close = await completed;
            if (completed == input)
            {
                // There is no pending receive to abort. Stop the only sender before sending a close frame.
                await cancellation.CancelAsync();
                try { await feed; }
                catch (OperationCanceledException) { }
            }
            await CloseOutputAsync(socket, close);
            if (completed == feed)
            {
                // Cancelling ReceiveAsync can abort a WebSocket. Deliver policy closure first and
                // allow a bounded close handshake so browsers can stop reconnecting on revocation.
                await input.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
            }
            else if (close == WebSocketCloseStatus.PolicyViolation)
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                await RejectInputAsync(socket, handshake.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or TimeoutException)
        {
            close = WebSocketCloseStatus.NormalClosure;
        }
        catch (Exception)
        {
            // Do not expose database, identity, or transport exception details to the browser.
            close = WebSocketCloseStatus.InternalServerError;
        }
        finally
        {
            if (!input.IsCompleted && socket.State == WebSocketState.Open)
            {
                try { await CloseOutputAsync(socket, close); }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
            }
            await cancellation.CancelAsync();
            try { await Task.WhenAll(feed, input); }
            catch (Exception) { /* Both workers are observed before disposing their resources. */ }
        }
    }

    private static async Task CloseOutputAsync(WebSocket socket, WebSocketCloseStatus close)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseOutputAsync(close, "Status feed closed.", timeout.Token);
    }

    private static bool IsSameOrigin(HttpRequest request, string? publicOrigin)
    {
        var origins = request.Headers.Origin;
        if (origins.Count != 1 || origins[0] is not { } origin
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        // Behind TLS termination, trust only the origin explicitly saved by an administrator.
        // Otherwise use the direct request origin. Never trust client-supplied forwarded headers.
        return string.Equals(origin, publicOrigin ?? $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WebSocketCloseStatus> SendStatusAsync(WebSocket socket, IServiceScopeFactory scopes,
        Guid actorId, string stamp, DateTimeOffset expires, TimeProvider clock, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            timer.Period = TimeSpan.FromSeconds((await services.GetRequiredService<OperationalPolicyService>().GetAsync(token)).Policy.StatusIntervalSeconds);
            var account = await services.GetRequiredService<AccountService>().FindAsync(actorId, token);
            if (clock.GetUtcNow() >= expires || account is null || !account.Enabled || account.SecurityStamp != stamp)
                return WebSocketCloseStatus.PolicyViolation;
            var ready = await services.GetRequiredService<StanzaDbContext>().Database.CanConnectAsync(token);
            int? agentCount = null;
            int? pendingAgentCount = null;
            if (account.Role is AccountRole.Admin or AccountRole.SystemAdmin)
            {
                var agents = await services.GetRequiredService<AgentAdministrationService>().ListAsync(actorId, token);
                agentCount = agents.Count;
                pendingAgentCount = agents.Count(agent => agent.Status == "pending");
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { databaseReady = ready, agentCount, pendingAgentCount });
            // The schema contains only three scalar values; cap it defensively as well.
            if (payload.Length > 256) return WebSocketCloseStatus.MessageTooBig;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, sendCancellation.Token);
        } while (await timer.WaitForNextTickAsync(token));
        return WebSocketCloseStatus.NormalClosure;
    }

    private static async Task<WebSocketCloseStatus> RejectInputAsync(WebSocket socket, CancellationToken token)
    {
        var result = await socket.ReceiveAsync(new byte[1].AsMemory(), token);
        return result.MessageType == WebSocketMessageType.Close
            ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation;
    }
}
