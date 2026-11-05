using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Transport;

public interface IAgentCommandDispatcher
{
    bool IsConnected(Guid agentId);
    Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default);
}

public sealed class AgentUnavailableException(string message = "The Agent is offline or unavailable.") : Exception(message);

public interface IAgentJobDispatcher
{
    Task<AgentJobInspection> InspectAsync(Guid agentId, Guid operationId, CancellationToken token = default);
    Task<AgentWorkResponse> AttachAsync(Guid agentId, Guid operationId, CancellationToken token = default);
    Task CancelAsync(Guid agentId, Guid operationId, string reason, CancellationToken token = default);
}

public interface IAgentWorkProgressSource
{
    event Func<Guid, string, Task>? Progress;
}

public interface IAgentWorkOutputSource
{
    event Func<Guid, AgentOutputChunk, Task>? Output;
}

public interface IAgentConnectionCatalog
{
    IReadOnlyList<Guid> ConnectedAgentIds { get; }
}
