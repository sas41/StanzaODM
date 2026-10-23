using System.ComponentModel.DataAnnotations;
using Stanza.ControlPlane.Deployments;

namespace Stanza.ControlPlane.Models;

public sealed record DeploymentListModel(IReadOnlyList<DeploymentDraftSummary> Drafts, bool CanCreate)
{
    public IReadOnlyDictionary<Guid, Operations.DeploymentRuntimeStatus> Runtime { get; init; } = new Dictionary<Guid, Operations.DeploymentRuntimeStatus>();
}

public sealed class CreateDeploymentModel
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Display(Name = "Target Agent")] public Guid? TargetAgentId { get; set; }
    public IReadOnlyList<DeploymentAgentOption> Agents { get; set; } = [];
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ComposeText { get; set; } = "services: {}\n";
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EnvironmentText { get; set; } = "";
}

public sealed class EditDeploymentModel
{
    public Guid Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public Guid? TargetAgentId { get; set; }
    public IReadOnlyList<DeploymentAgentOption> Agents { get; set; } = [];
    [Range(1, long.MaxValue)] public long ExpectedRevision { get; set; }
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ComposeText { get; set; }
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EnvironmentText { get; set; }
    public bool CanEditCompose { get; set; }
    public bool CanEditEnvironment { get; set; }
}

public sealed class DeploymentFileModel
{
    public Guid Id { get; set; }
    [Required, StringLength(1024)] public string Path { get; set; } = "";
    [Range(1, long.MaxValue)] public long ExpectedRevision { get; set; }
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Text { get; set; } = "";
    public bool IsNew { get; set; }
}
