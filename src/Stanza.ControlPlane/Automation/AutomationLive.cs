using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Terminals;

namespace Stanza.ControlPlane.Automation;

public static class AutomationLive
{
    public static string Version(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public static IEndpointRouteBuilder MapStanzaActionLive(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/live/deployment-actions", (HttpContext context) => StreamAsync(context, null)).RequireAuthorization();
        endpoints.MapGet("/live/actions/{id:guid}", (HttpContext context, Guid id) => StreamAsync(context, id)).RequireAuthorization();
        return endpoints;
    }

    private static async Task StreamAsync(HttpContext context, Guid? runId)
    {
        var origin = await context.RequestServices.GetRequiredService<InstanceSettingsService>().ReadPublicOriginAsync(context.RequestAborted);
        if (!TerminalExtensions.IsSameOrigin(context.Request, origin)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || auth.Properties?.ExpiresUtc is not { } expires)
        { context.Response.StatusCode = 401; return; }
        var stamp = context.User.FindFirstValue("security_stamp");
        if (runId is { } requestedRun)
        {
            try { await context.RequestServices.GetRequiredService<AutomationService>().ReadRunAsync(actor, requestedRun, context.RequestAborted); }
            catch (AutomationAccessException) { context.Response.StatusCode = 403; return; }
            catch (AutomationException) { context.Response.StatusCode = 404; return; }
        }
        var policy = (await context.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.RequestAborted)).Policy;
        using var lease = context.RequestServices.GetRequiredService<LiveConnectionLeases>().TryAcquire(actor, policy.MaximumConnectionsPerUser, policy.MaximumConnections);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var input = socket.ReceiveAsync(new byte[1].AsMemory(), lifetime.Token).AsTask();
        string? previous = null;
        try
        {
            while (!lifetime.IsCancellationRequested && !input.IsCompleted)
            {
                await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                var account = await scope.ServiceProvider.GetRequiredService<AccountService>().FindAsync(actor, lifetime.Token);
                if (account is null || !account.Enabled || account.SecurityStamp != stamp || DateTimeOffset.UtcNow >= expires)
                { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked.", lifetime.Token); break; }
                object data = runId is { } id
                    ? await scope.ServiceProvider.GetRequiredService<AutomationService>().ReadRunAsync(actor, id, lifetime.Token)
                    : await scope.ServiceProvider.GetRequiredService<DeploymentActionOverview>().ReadAsync(actor, lifetime.Token);
                var version = Version(data);
                if (version != previous)
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); send.CancelAfter(TimeSpan.FromSeconds(5));
                    // Content is fetched by HTMX after this invalidation. The socket never exposes private inputs.
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { version }), WebSocketMessageType.Text, true, send.Token);
                    previous = version;
                }
                await Task.WhenAny(input, Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token));
            }
        }
        catch (AutomationAccessException) { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked.", CancellationToken.None); }
        catch (AutomationException) { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Run unavailable.", CancellationToken.None); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        finally { await lifetime.CancelAsync(); try { await input; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } }
    }
}
