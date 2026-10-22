using System.Text.Json;
using System.Threading.Channels;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Terminals;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Transport;

public static class TerminalAgentBridgeExtensions
{
    public static IServiceCollection AddStanzaAgentTerminalBridge(this IServiceCollection services)
    {
        services.AddScoped<IAgentTerminalTransport, TerminalAgentBridge>();
        return services;
    }
}

public sealed class TerminalAgentBridge(AgentCommandBroker broker, AgentPolicyService policies) : IAgentTerminalTransport
{
    public async Task<IAgentTerminalSession> OpenAsync(Guid agentId, Guid deploymentId, AgentExecution execution, CancellationToken cancellationToken)
    {
        var policy = await policies.RuntimeAsync(agentId, cancellationToken);
        if (!policy.ExecutionEnabled) throw new IOException("Agent execution is disabled.");
        return await broker.OpenTerminalAsync(agentId, deploymentId, execution, policy, cancellationToken);
    }
}

internal sealed class BrokerTerminalSession(Guid id, Func<AgentWireMessage, CancellationToken, Task> send,
    Action<Guid> remove, bool readOnly = false) : IAgentTerminalSession
{
    private readonly Channel<TerminalOutput> output = Channel.CreateBounded<TerminalOutput>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    private int closing;
    public Task Opened => opened.Task;
    public bool Receive(AgentWireMessage message)
    {
        if (message.Type == "terminal.opened") { opened.TrySetResult(); return true; }
        if (message.Type == "terminal.output")
        {
            if (!opened.Task.IsCompletedSuccessfully || message.Data is not { Length: > 0 and <= 87384 }) throw new IOException("Invalid terminal output.");
            if (Volatile.Read(ref closing) != 0) return true; // Do not allocate one close task per late output frame.
            var bytes = Convert.FromBase64String(message.Data);
            if (bytes.Length is 0 or > TerminalProtocol.MaximumInputBytes) throw new IOException("Invalid terminal output.");
            return output.Writer.TryWrite(new(bytes));
        }
        if (message.Type != "terminal.exited" || message.Data is not { Length: > 0 and <= 12 } ||
            !int.TryParse(message.Data, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var code))
            throw new IOException("Invalid terminal exit.");
        opened.TrySetException(new IOException("Agent could not open the terminal."));
        var accepted = output.Writer.TryWrite(new(ReadOnlyMemory<byte>.Empty, code));
        Complete();
        return accepted;
    }
    public void Complete()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        opened.TrySetException(new IOException("Agent terminal disconnected."));
        output.Writer.TryComplete();
        ended.TrySetResult();
        remove(id);
    }
    public async ValueTask<TerminalOutput> ReadOutputAsync(CancellationToken cancellationToken)
    {
        try { return await output.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException) { throw new IOException("Agent terminal disconnected."); }
    }
    public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        if (readOnly) throw new IOException("Container log streams are read-only.");
        if (input.Length is 0 or > TerminalProtocol.MaximumInputBytes || Volatile.Read(ref closed) != 0 || Volatile.Read(ref closing) != 0) throw new IOException("Terminal is closed or input exceeds limit.");
        await send(new("terminal.input", id, Data: Convert.ToBase64String(input.Span)), cancellationToken);
    }
    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        if (readOnly) throw new IOException("Container log streams cannot be resized.");
        if (!TerminalProtocol.ValidDimensions(columns, rows) || Volatile.Read(ref closed) != 0 || Volatile.Read(ref closing) != 0) throw new IOException("Invalid terminal resize.");
        await send(new("terminal.resize", id, Data: JsonSerializer.Serialize(new[] { columns, rows })), cancellationToken);
    }
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed) != 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (Interlocked.Exchange(ref closing, 1) == 0) await send(new("terminal.close", id), timeout.Token);
            await ended.Task.WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
        finally { Complete(); }
    }
    public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);
}
