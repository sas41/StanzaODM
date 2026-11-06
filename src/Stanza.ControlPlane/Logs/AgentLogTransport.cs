using System.Threading.Channels;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Logs;

public interface IAgentLogTransport
{
    Task<IAgentLogSession> OpenAsync(Guid agentId, Guid deploymentId, AgentExecution target, CancellationToken token);
}
public interface IAgentLogSession : IAsyncDisposable
{
    ValueTask<LogOutput> ReadOutputAsync(CancellationToken token);
    ValueTask CloseAsync(CancellationToken token);
}
public sealed record LogOutput(ReadOnlyMemory<byte> Data, int? EndCode = null)
{
    public override string ToString() => $"LogOutput {{ Bytes = {Data.Length}, EndCode = {EndCode} }}";
}

public sealed class AgentLogTransport(AgentCommandBroker broker, AgentPolicyService policies) : IAgentLogTransport
{
    public async Task<IAgentLogSession> OpenAsync(Guid agentId, Guid deploymentId, AgentExecution target, CancellationToken token) =>
        await broker.OpenLogsAsync(agentId, deploymentId, target, await policies.RuntimeAsync(agentId, token), token);
}

internal sealed class BrokerLogSession(Guid id, Func<AgentWireMessage, CancellationToken, Task> send, Action<Guid> remove) : IAgentLogSession
{
    private readonly Channel<LogOutput> output = Channel.CreateBounded<LogOutput>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed, closing;
    public Task Opened => opened.Task;
    public bool Receive(AgentWireMessage message)
    {
        if (message.Type == "log.opened") { opened.TrySetResult(); return true; }
        if (message.Type == "log.output")
        {
            if (!opened.Task.IsCompletedSuccessfully || message.Data is not { Length: > 0 and <= 87384 }) throw new IOException("Invalid log output.");
            if (Volatile.Read(ref closing) != 0) return true;
            var bytes = Convert.FromBase64String(message.Data);
            if (bytes.Length is 0 or > AgentLogProtocol.MaximumChunk) throw new IOException("Invalid log output.");
            return output.Writer.TryWrite(new(bytes));
        }
        if (message.Type != "log.ended" || message.Data is not { Length: > 0 and <= 12 } ||
            !int.TryParse(message.Data, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var code))
            throw new IOException("Invalid log ending.");
        opened.TrySetException(new IOException("Agent could not open the log stream."));
        var accepted = output.Writer.TryWrite(new(ReadOnlyMemory<byte>.Empty, code)); Complete(); return accepted;
    }
    public void Complete()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        opened.TrySetException(new IOException("Agent log stream disconnected.")); output.Writer.TryComplete(); ended.TrySetResult(); remove(id);
    }
    public async ValueTask<LogOutput> ReadOutputAsync(CancellationToken token)
    {
        try { return await output.Reader.ReadAsync(token); }
        catch (ChannelClosedException) { throw new IOException("Agent log stream disconnected."); }
    }
    public async ValueTask CloseAsync(CancellationToken token)
    {
        if (Volatile.Read(ref closed) != 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (Interlocked.Exchange(ref closing, 1) == 0) await send(new("log.close", id), timeout.Token);
            await ended.Task.WaitAsync(timeout.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
        finally { Complete(); }
    }
    public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);
}
