using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Stanza.Tests.Live;

internal sealed class BlockedStatusSocket : WebSocket
{
    public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebSocketState state = WebSocketState.Open;
    public bool SendWasCancelled { get; private set; }
    public int PayloadSize { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; closing.TrySetResult(); }
    public override void Dispose() { state = WebSocketState.Closed; closing.TrySetResult(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        state = WebSocketState.CloseSent;
        closing.TrySetResult();
        return Task.CompletedTask;
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        PayloadSize = buffer.Count;
        Sending.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) { SendWasCancelled = true; throw; }
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await closing.Task.WaitAsync(cancellationToken);
        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "");
    }
}

internal sealed class BlockedSocketHosting(BlockedStatusSocket socket) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path == "/live/status")
                context.Features.Set<IHttpWebSocketFeature>(new Feature(socket));
            await continuation();
        });
        next(app);
    };

    private sealed class Feature(BlockedStatusSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult<WebSocket>(socket);
    }
}
