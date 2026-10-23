using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Operations;

public sealed record OperationSubmission(Guid ActorId, Guid? DeploymentId, string Kind, string Trigger = "manual",
    Guid? AgentId = null, AgentExecution? Execution = null,
    IReadOnlyDictionary<string, string>? SourceOverrides = null, IReadOnlyDictionary<string, string>? ImageOverrides = null);
public sealed record OperationSummary(Guid Id, Guid ActorId, Guid? DeploymentId, Guid AgentId, string Kind,
    string State, string Trigger, long CreatedAt, long? CompletedAt, string? Code);
public interface IOperationService
{
    Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default);
    Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default);
    Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default);
}
