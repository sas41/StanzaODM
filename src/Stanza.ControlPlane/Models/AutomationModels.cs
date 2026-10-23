using System.ComponentModel.DataAnnotations;
using Stanza.ControlPlane.Automation;

namespace Stanza.ControlPlane.Models;

public sealed class ActionEditorModel
{
    public Guid? Id { get; set; }
    public long Revision { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    [Required] public string Concurrency { get; set; } = "queue";
    [Required, StringLength(131072)] public string PlanJson { get; set; } = "{\"steps\":[{\"kind\":\"deploy\",\"deploymentId\":null,\"failure\":\"stop\"}],\"parameters\":[]}";
}
public sealed class ScheduleEditorModel
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public Guid? DeploymentId { get; set; }
    public Guid? ActionId { get; set; }
    public string Operation { get; set; } = "deploy";
    [Required, StringLength(256)] public string Expression { get; set; } = "0 * * * *";
    [Required, StringLength(100)] public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; }
    public string Overlap { get; set; } = "skip";
    public string MissedRun { get; set; } = "skip";
}
public sealed class WebhookEditorModel
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public Guid DeploymentId { get; set; }
    public Guid? ActionId { get; set; }
    public string Operation { get; set; } = "deploy";
    public string Provider { get; set; } = "github";
    public string Events { get; set; } = "push";
    public string Refs { get; set; } = "";
    public string ParameterMapping { get; set; } = "{}";
    public bool Enabled { get; set; }
}
public sealed class ImagePolicyEditorModel
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public Guid DeploymentId { get; set; }
    public string Services { get; set; } = "";
    public string Mode { get; set; } = "notify-only";
    public bool Enabled { get; set; }
    public int FrequencyMinutes { get; set; } = 60;
    public int FailureBackoffMinutes { get; set; } = 60;
}
public sealed record WebhookSecretModel(Guid Id, string Secret);
public sealed record WebhookHistoryModel(Guid Id, IReadOnlyList<WebhookDelivery> Deliveries);
public sealed record ActionInvocationModel(Guid Id, long Revision, string Name, ActionPlan Plan, IReadOnlyDictionary<string, string> Values, string? Error = null);
public sealed record ActionAccessSubject(Guid Id, string Name, bool IsGroup);
public sealed record ActionAccessModel(Guid Id, string Name, IReadOnlyList<ActionAccessSubject> Subjects, IReadOnlyList<Security.GrantSummary> Grants);
