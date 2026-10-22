using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Terminals;

public interface IAgentTerminalTransport
{
    // The adapter must verify the authenticated, connected Agent and deployment Compose labels.
    // Open allocates a real container PTY, never a host process or concatenated shell command.
    Task<IAgentTerminalSession> OpenAsync(Guid agentId, Guid deploymentId, AgentExecution execution, CancellationToken cancellationToken);
}

public interface IAgentTerminalSession : IAsyncDisposable
{
    // Each read is at most MaximumInputBytes. A null exit code means more output follows;
    // a non-null exit code is the final read (which may also carry final output). Reads must
    // block until data/exit, not return empty non-final frames. All methods honor cancellation;
    // Close/Dispose terminate the remote PTY and release bounded transport queues.
    ValueTask<TerminalOutput> ReadOutputAsync(CancellationToken cancellationToken);
    ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public sealed record TerminalOutput(ReadOnlyMemory<byte> Data, int? ExitCode = null)
{
    public override string ToString() => $"TerminalOutput {{ Bytes = {Data.Length}, ExitCode = {ExitCode} }}";
}
