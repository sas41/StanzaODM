using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Terminals;

namespace Stanza.ControlPlane.Logs;

public static class LogExtensions
{
    public static IServiceCollection AddStanzaLogs(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LiveConnectionLeases>();
        services.TryAddScoped<LogAccess>();
        services.TryAddScoped<AuditService>();
        return services;
    }

    public static IEndpointRouteBuilder MapStanzaLogs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/live/logs/{deploymentId:guid}", HandleAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, Guid deploymentId)
    {
        context.Response.Headers.CacheControl = "no-store";
        var services = context.RequestServices;
        var ct = context.RequestAborted;
        var origin = await services.GetRequiredService<InstanceSettingsService>().ReadPublicOriginAsync(ct);
        if (!TerminalExtensions.IsSameOrigin(context.Request, origin)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)
            || context.User.FindFirstValue("security_stamp") is not { Length: > 0 } stamp
            || auth.Properties?.ExpiresUtc is not { } expires)
        { context.Response.StatusCode = 401; return; }
        var access = services.GetRequiredService<LogAccess>();
        LogTarget target;
        try { target = await access.RequireAsync(actor, deploymentId, ct); }
        catch (LogAccessException error) { context.Response.StatusCode = error.StatusCode; return; }
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
        int? endCode = null;
        var sessionId = Guid.NewGuid();
        // Exact IDs/target fit the audit schema; output, credentials and container contents do not belong here.
        var resource = $"d:{deploymentId:N};a:{target.AgentId:N}";
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opening.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await ReceiveAsync(socket, opening.Token);
            if (message is null) { status = WebSocketCloseStatus.NormalClosure; outcome = "cancelled"; return; }
            var execution = LogProtocol.Execution(message);
            resource += $";{(execution.Service is null ? 'c' : 's')}:{execution.Service ?? execution.Container}";
            if (!await CurrentAsync(scopes, actor, stamp, expires, target, opening.Token)) throw new LogAccessException(403, "Access revoked.");
            session = await transport.OpenAsync(target.AgentId, deploymentId, execution, opening.Token);
            await AuditAsync(scopes, actor, "log.open", resource, $"opened;session:{sessionId:N}", opening.Token);
            await SendAsync(socket, new { type = "ready" }, workers.Token);
            input = ReadOnlyInputAsync(socket, scopes, actor, stamp, expires, target, receiver.Token);
            output = OutputAsync(socket, session, code => endCode = code, workers.Token);
            monitor = MonitorAsync(scopes, actor, stamp, expires, target, clock, workers.Token);
            var completed = await Task.WhenAny(input, output, monitor);
            status = await completed;
            outcome = completed == output && status == WebSocketCloseStatus.NormalClosure ? $"ended:{endCode}"
                : status == WebSocketCloseStatus.PolicyViolation ? "revoked-or-invalid-input" : "disconnected";
        }
        catch (Exception error) when (error is LogProtocolException or JsonException)
        { status = WebSocketCloseStatus.PolicyViolation; outcome = "invalid-message"; }
        catch (LogFrameException) { status = WebSocketCloseStatus.MessageTooBig; outcome = "message-too-big"; }
        catch (LogAccessException) { status = WebSocketCloseStatus.PolicyViolation; outcome = "revoked"; }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        { status = WebSocketCloseStatus.NormalClosure; outcome = "disconnected-or-timeout"; }
        catch (Exception) { status = WebSocketCloseStatus.InternalServerError; outcome = "transport-failed"; }
        finally
        {
            // Stop the sole sender before writing the close frame. Cancelling a pending WebSocket
            // receive can abort the socket, so leave it running until the close frame is delivered.
            await workers.CancelAsync();
            await ObserveAsync(output, monitor);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(status, "Container logs closed.", close.Token);
                    if (input is not null) await input.WaitAsync(close.Token);
                }
            }
            catch (Exception) { /* Do not expose transport or identity exception details. */ }
            await receiver.CancelAsync();
            await ObserveAsync(input);
            if (session is not null)
            {
                try { using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await session.CloseAsync(close.Token); }
                catch (Exception) { outcome = "cleanup-failed"; }
                try { await session.DisposeAsync(); } catch (Exception) { outcome = "cleanup-failed"; }
            }
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await AuditAsync(scopes, actor, "log.closed", resource, $"{outcome};session:{sessionId:N}", timeout.Token); }
            catch (Exception) { services.GetRequiredService<ILogger<LogAccess>>().LogError("Container log closure audit persistence failed."); }
        }
    }

    private static async Task<WebSocketCloseStatus> ReadOnlyInputAsync(WebSocket socket, IServiceScopeFactory scopes,
        Guid actor, string stamp, DateTimeOffset expires, LogTarget target, CancellationToken ct)
    {
        var message = await ReceiveAsync(socket, ct);
        if (message is null) return WebSocketCloseStatus.NormalClosure;
        if (!await CurrentAsync(scopes, actor, stamp, expires, target, ct) || !LogProtocol.IsClose(message))
            return WebSocketCloseStatus.PolicyViolation;
        return WebSocketCloseStatus.NormalClosure;
    }

    private static async Task<WebSocketCloseStatus> OutputAsync(WebSocket socket, IAgentTerminalSession session, Action<int> ended, CancellationToken ct)
    {
        while (true)
        {
            var output = await session.ReadOutputAsync(ct);
            if (output.Data.Length > LogProtocol.MaximumOutputBytes) throw new LogFrameException();
            if (output.Data.IsEmpty && output.ExitCode is null) throw new InvalidOperationException("The Agent returned an empty non-final log frame.");
            if (!output.Data.IsEmpty) await SendAsync(socket, new { type = "output", data = Convert.ToBase64String(output.Data.Span) }, ct);
            if (output.ExitCode is { } code)
            {
                ended(code);
                await SendAsync(socket, new { type = "end", code }, ct);
                return WebSocketCloseStatus.NormalClosure;
            }
        }
    }

    private static async Task<WebSocketCloseStatus> MonitorAsync(IServiceScopeFactory scopes, Guid actor, string stamp,
        DateTimeOffset expires, LogTarget target, TimeProvider clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        while (await timer.WaitForNextTickAsync(ct))
            if (!await CurrentAsync(scopes, actor, stamp, expires, target, ct)) return WebSocketCloseStatus.PolicyViolation;
        return WebSocketCloseStatus.NormalClosure;
    }

    private static async Task<bool> CurrentAsync(IServiceScopeFactory scopes, Guid actor, string stamp, DateTimeOffset expires, LogTarget target, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LogAccess>().IsCurrentAsync(actor, stamp, expires, target, ct);
    }

    private static async Task AuditAsync(IServiceScopeFactory scopes, Guid actor, string action, string resource, string result, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditService>().WriteAsync(actor, action, resource, result, ct);
    }

    private static async Task<LogMessage?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[LogProtocol.MaximumClientMessageBytes + 1];
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new LogProtocolException();
            count += result.Count;
            if (count > LogProtocol.MaximumClientMessageBytes) throw new LogFrameException();
            if (result.EndOfMessage) return LogProtocol.Parse(buffer.AsMemory(0, count));
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

    private sealed class LogFrameException : Exception;
}
