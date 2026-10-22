using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;

namespace Stanza.ControlPlane.Terminals;

public static class TerminalExtensions
{
    public static IServiceCollection AddStanzaTerminals(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LiveConnectionLeases>();
        services.TryAddScoped<TerminalAccess>();
        services.TryAddScoped<AuditService>();
        return services;
    }

    public static IEndpointRouteBuilder MapStanzaTerminals(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/live/terminal/{deploymentId:guid}", HandleAsync).RequireAuthorization();
        return endpoints;
    }

    public static bool IsSameOrigin(HttpRequest request, string? publicOrigin)
    {
        var origins = request.Headers.Origin;
        return origins.Count == 1 && origins[0] is { } origin
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && string.Equals(origin, publicOrigin ?? $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task HandleAsync(HttpContext context, Guid deploymentId)
    {
        context.Response.Headers.CacheControl = "no-store";
        var services = context.RequestServices;
        var ct = context.RequestAborted;
        var origin = await services.GetRequiredService<InstanceSettingsService>().ReadPublicOriginAsync(ct);
        if (!IsSameOrigin(context.Request, origin)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)
            || context.User.FindFirstValue("security_stamp") is not { Length: > 0 } stamp
            || auth.Properties?.ExpiresUtc is not { } expires)
        { context.Response.StatusCode = 401; return; }
        var access = services.GetRequiredService<TerminalAccess>();
        TerminalTarget target;
        try { target = await access.RequireAsync(actor, deploymentId, ct); }
        catch (TerminalAccessException error) { context.Response.StatusCode = error.StatusCode; return; }
        if (!await access.IsCurrentAsync(actor, stamp, expires, target, ct)) { context.Response.StatusCode = 403; return; }
        var operational = services.GetService<OperationalPolicyService>();
        var policy = operational is null ? new OperationalPolicy() : (await operational.GetAsync(ct)).Policy;
        using var lease = services.GetRequiredService<LiveConnectionLeases>().TryAcquire(actor, policy.MaximumConnectionsPerUser, policy.MaximumConnections);
        if (lease is null) { context.Response.StatusCode = 429; return; }
        var transport = services.GetRequiredService<IAgentTerminalTransport>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var clock = services.GetRequiredService<TimeProvider>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var receiver = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IAgentTerminalSession? session = null;
        Task<WebSocketCloseStatus>? input = null, output = null, monitor = null;
        var status = WebSocketCloseStatus.InternalServerError;
        var outcome = "failed";
        int? exitCode = null;
        var sessionId = Guid.NewGuid();
        // Compact keys keep exact deployment/Agent/target IDs within Audit.Resource's 200-character
        // limit. The session correlation ID is in Result. Never audit executable/args or contents.
        var resource = $"d:{deploymentId:N};a:{target.AgentId:N}";
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opening.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await ReceiveAsync(socket, TerminalProtocol.MaximumOpenBytes, opening.Token);
            if (message is null) { status = WebSocketCloseStatus.NormalClosure; outcome = "cancelled"; return; }
            var execution = TerminalProtocol.Execution(message);
            resource += $";{(execution.Service is null ? 'c' : 's')}:{execution.Service ?? execution.Container}";
            if (!await CurrentAsync(scopes, actor, stamp, expires, target, opening.Token)) throw new TerminalAccessException(403, "Access revoked.");
            session = await transport.OpenAsync(target.AgentId, deploymentId, execution, opening.Token);
            await session.ResizeAsync(message.Columns, message.Rows, opening.Token);
            await AuditAsync(scopes, actor, "terminal.opened", resource, $"opened;session:{sessionId:N}", opening.Token);
            await SendAsync(socket, new { type = "ready" }, workers.Token);
            input = InputAsync(socket, session, scopes, actor, stamp, expires, target, receiver.Token);
            output = OutputAsync(socket, session, code => exitCode = code, workers.Token);
            monitor = MonitorAsync(scopes, actor, stamp, expires, target, clock, workers.Token);
            var completed = await Task.WhenAny(input, output, monitor);
            status = await completed;
            outcome = completed == output && status == WebSocketCloseStatus.NormalClosure ? $"exited:{exitCode}"
                : status == WebSocketCloseStatus.PolicyViolation ? "revoked-or-invalid-input" : "disconnected";
        }
        catch (Exception error) when (error is TerminalProtocolException or JsonException or FormatException)
        { status = WebSocketCloseStatus.PolicyViolation; outcome = "invalid-message"; }
        catch (TerminalFrameException) { status = WebSocketCloseStatus.MessageTooBig; outcome = "message-too-big"; }
        catch (TerminalAccessException) { status = WebSocketCloseStatus.PolicyViolation; outcome = "revoked"; }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        { status = WebSocketCloseStatus.NormalClosure; outcome = "disconnected-or-timeout"; }
        catch (Exception) { status = WebSocketCloseStatus.InternalServerError; outcome = "transport-failed"; }
        finally
        {
            // Stop the sole sender before writing a close frame. Leave the receiver running until
            // the close frame is delivered; cancelling a WebSocket receive can abort the socket.
            await workers.CancelAsync();
            await ObserveAsync(output, monitor);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(status, "Container terminal closed.", close.Token);
                    if (input is not null) await input.WaitAsync(close.Token);
                }
            }
            catch (Exception) { /* No transport or identity details are sent to the browser. */ }
            await receiver.CancelAsync();
            await ObserveAsync(input);
            if (session is not null)
            {
                try { using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await session.CloseAsync(close.Token); }
                catch (Exception) { outcome = "cleanup-failed"; }
                try { await session.DisposeAsync(); } catch (Exception) { outcome = "cleanup-failed"; }
            }
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await AuditAsync(scopes, actor, "terminal.closed", resource, $"{outcome};session:{sessionId:N}", timeout.Token); }
            catch (Exception) { services.GetRequiredService<ILogger<TerminalAccess>>().LogError("Terminal closure audit persistence failed."); }
        }
    }

    private static async Task<WebSocketCloseStatus> InputAsync(WebSocket socket, IAgentTerminalSession session,
        IServiceScopeFactory scopes, Guid actor, string stamp, DateTimeOffset expires, TerminalTarget target, CancellationToken ct)
    {
        while (await ReceiveAsync(socket, TerminalProtocol.MaximumFrameBytes, ct) is { } message)
        {
            if (!await CurrentAsync(scopes, actor, stamp, expires, target, ct)) return WebSocketCloseStatus.PolicyViolation;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            switch (message.Type)
            {
                case "input": await session.WriteInputAsync(TerminalProtocol.Input(message), deadline.Token); break;
                case "resize" when TerminalProtocol.ValidDimensions(message.Columns, message.Rows):
                    await session.ResizeAsync(message.Columns, message.Rows, deadline.Token); break;
                case "close": return WebSocketCloseStatus.NormalClosure;
                default: throw new TerminalProtocolException();
            }
        }
        return WebSocketCloseStatus.NormalClosure;
    }

    private static async Task<WebSocketCloseStatus> OutputAsync(WebSocket socket, IAgentTerminalSession session, Action<int> exited, CancellationToken ct)
    {
        while (true)
        {
            var output = await session.ReadOutputAsync(ct);
            if (output.Data.Length > TerminalProtocol.MaximumInputBytes) throw new TerminalFrameException();
            if (output.Data.IsEmpty && output.ExitCode is null) throw new TerminalProtocolException();
            if (!output.Data.IsEmpty) await SendAsync(socket, new { type = "output", data = Convert.ToBase64String(output.Data.Span) }, ct);
            if (output.ExitCode is { } exit)
            {
                exited(exit);
                await SendAsync(socket, new { type = "exit", exitCode = exit }, ct);
                return WebSocketCloseStatus.NormalClosure;
            }
        }
    }

    private static async Task<WebSocketCloseStatus> MonitorAsync(IServiceScopeFactory scopes, Guid actor, string stamp,
        DateTimeOffset expires, TerminalTarget target, TimeProvider clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        while (await timer.WaitForNextTickAsync(ct))
            if (!await CurrentAsync(scopes, actor, stamp, expires, target, ct)) return WebSocketCloseStatus.PolicyViolation;
        return WebSocketCloseStatus.NormalClosure;
    }

    private static async Task<bool> CurrentAsync(IServiceScopeFactory scopes, Guid actor, string stamp,
        DateTimeOffset expires, TerminalTarget target, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TerminalAccess>().IsCurrentAsync(actor, stamp, expires, target, ct);
    }

    private static async Task AuditAsync(IServiceScopeFactory scopes, Guid actor, string action, string resource, string result, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditService>().WriteAsync(actor, action, resource, result, ct);
    }

    private static async Task<TerminalMessage?> ReceiveAsync(WebSocket socket, int maximum, CancellationToken ct)
    {
        var buffer = new byte[maximum + 1];
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new TerminalProtocolException();
            count += result.Count;
            if (count > maximum) throw new TerminalFrameException();
            if (result.EndOfMessage) return TerminalProtocol.Parse(buffer.AsMemory(0, count));
        }
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
    }

    private static async Task ObserveAsync(params Task?[] tasks)
    {
        foreach (var task in tasks) if (task is not null) try { await task; } catch (Exception) { }
    }

    private sealed class TerminalFrameException : Exception;
}
