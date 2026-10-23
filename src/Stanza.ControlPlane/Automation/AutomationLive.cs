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
        endpoints.MapGet("/live/actions/{id:guid}/output", (HttpContext context, Guid id) => StreamAsync(context, id, true)).RequireAuthorization();
        return endpoints;
    }

    private static async Task StreamAsync(HttpContext context, Guid? runId, bool includeOutput = false)
    {
        var cursors = new Dictionary<Guid, long>();
        if (includeOutput && context.Request.Query["after"].ToString() is { Length: > 0 } text)
        {
            try
            {
                if (text.Length > 8192) throw new JsonException();
                cursors = JsonSerializer.Deserialize<Dictionary<Guid, long>>(text) ?? throw new JsonException();
                if (cursors.Count > 51 || cursors.Any(x => x.Key == Guid.Empty || x.Value < 0)) throw new JsonException();
            }
            catch (JsonException) { context.Response.StatusCode = 400; return; }
        }
        var legacySent = new HashSet<Guid>();
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
                var batch = includeOutput && data is AutomationRunDetails detail
                    ? await scope.ServiceProvider.GetRequiredService<ActionRunOutputFeed>().ReadAsync(actor, detail, cursors, legacySent, lifetime.Token) : null;
                if (version != previous || batch is not null)
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); send.CancelAfter(TimeSpan.FromSeconds(5));
                    // Content is fetched by HTMX after this invalidation. The socket never exposes private inputs.
                    var payload = batch is not null && data is AutomationRunDetails run
                        ? JsonSerializer.SerializeToUtf8Bytes(new { version, state = run.Run.State, operations = batch.Operations }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        : JsonSerializer.SerializeToUtf8Bytes(new { version });
                    await socket.SendAsync(payload, WebSocketMessageType.Text, true, send.Token);
                    previous = version;
                }
                if (batch is not null && !batch.HasMore && batch.Operations.All(x => Operations.OperationService.Terminal(x.State)) &&
                    data is AutomationRunDetails final && final.Run.State is not ("queued" or "running"))
                { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Run finished.", lifetime.Token); break; }
                if (batch?.HasMore != true) await Task.WhenAny(input, Task.Delay(TimeSpan.FromMilliseconds(includeOutput ? 250 : 2000), lifetime.Token));
            }
        }
        catch (AutomationAccessException) { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked.", CancellationToken.None); }
        catch (AutomationException) { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Run unavailable.", CancellationToken.None); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        finally { await lifetime.CancelAsync(); try { await input; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } }
    }
}
