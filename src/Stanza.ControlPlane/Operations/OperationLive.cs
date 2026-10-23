using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Terminals;

namespace Stanza.ControlPlane.Operations;

public static class OperationLive
{
    public static IEndpointRouteBuilder MapStanzaOperationLive(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/live/operations/{id:guid}", async (HttpContext context, Guid id) =>
        {
            var origin = await context.RequestServices.GetRequiredService<InstanceSettingsService>().ReadPublicOriginAsync(context.RequestAborted);
            if (!TerminalExtensions.IsSameOrigin(context.Request, origin)) { context.Response.StatusCode = 403; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || auth.Properties?.ExpiresUtc is not { } expires) { context.Response.StatusCode = 401; return; }
            var stamp = context.User.FindFirstValue("security_stamp");
            try { await context.RequestServices.GetRequiredService<OperationService>().DetailAsync(actor, id, context.RequestAborted); }
            catch (OperationException) { context.Response.StatusCode = 403; return; }
            var policy = (await context.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.RequestAborted)).Policy;
            using var lease = context.RequestServices.GetRequiredService<LiveConnectionLeases>().TryAcquire(actor, policy.MaximumConnectionsPerUser, policy.MaximumConnections);
            if (lease is null) { context.Response.StatusCode = 429; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var cursor = long.TryParse(context.Request.Query["after"], out var after) && after >= 0 ? after : 0;
            var input = socket.ReceiveAsync(new byte[1].AsMemory(), lifetime.Token).AsTask();
            try
            {
                while (!lifetime.IsCancellationRequested && !input.IsCompleted)
                {
                    await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                    var account = await scope.ServiceProvider.GetRequiredService<AccountService>().FindAsync(actor, lifetime.Token);
                    if (account is null || !account.Enabled || account.SecurityStamp != stamp || DateTimeOffset.UtcNow >= expires)
                    { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked.", lifetime.Token); break; }
                    var detail = await scope.ServiceProvider.GetRequiredService<OperationService>().DetailAsync(actor, id, lifetime.Token);
                    var chunks = detail.OutputAllowed
                        ? await scope.ServiceProvider.GetRequiredService<OperationOutputStore>().ReadAsync(id, cursor, ct: lifetime.Token)
                        : [];
                    if (chunks.Count > 0) cursor = chunks[^1].Sequence;
                    var hasChunks = cursor > 0;
                    var output = !hasChunks ? detail.Output : null;
                    var stage = await scope.ServiceProvider.GetRequiredService<Persistence.StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == id).Select(x => x.Stage).SingleAsync(lifetime.Token);
                    var message = JsonSerializer.SerializeToUtf8Bytes(new { state = detail.Summary.State, stage, code = detail.Summary.Code, output,
                        chunks = chunks.Select(c => new { sequence = c.Sequence, stream = c.Stream, text = c.Text }), outputAllowed = detail.OutputAllowed });
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); send.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(message, WebSocketMessageType.Text, true, send.Token);
                    if (OperationService.Terminal(detail.Summary.State) && chunks.Count < 64) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Operation finished.", send.Token); break; }
                    if (chunks.Count < 64) await Task.WhenAny(input, Task.Delay(TimeSpan.FromMilliseconds(200), lifetime.Token));
                }
            }
            catch (OperationException) { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked.", CancellationToken.None); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
            finally { await lifetime.CancelAsync(); try { await input; } catch (Exception error) when (error is OperationCanceledException or WebSocketException) { } }
        }).RequireAuthorization();
        return endpoints;
    }
}
