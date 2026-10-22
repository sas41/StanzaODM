namespace Stanza.ControlPlane.Models;

public sealed record SystemStatusModel(bool DatabaseReady, int? AgentCount, int? PendingAgentCount);
