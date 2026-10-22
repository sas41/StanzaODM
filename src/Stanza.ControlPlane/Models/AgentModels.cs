using System.ComponentModel.DataAnnotations;
using Stanza.ControlPlane.Agents;

namespace Stanza.ControlPlane.Models;

public sealed record AgentListModel(IReadOnlyList<AgentSummary> Agents);
public sealed class AgentEnvironmentModel
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
    [Range(0, long.MaxValue)] public long Version { get; set; }
}
