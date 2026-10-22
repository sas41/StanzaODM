using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Transport;

public interface IAgentCommandDispatcher
{
    bool IsConnected(Guid agentId);
    Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default);
}

public sealed class AgentUnavailableException(string message = "The Agent is offline or unavailable.") : Exception(message);

public interface IAgentWorkProgressSource
{
    event Func<Guid, string, Task>? Progress;
}

public interface IAgentConnectionCatalog
{
    IReadOnlyList<Guid> ConnectedAgentIds { get; }
}
