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
    [StringLength(131072)] public string? PlanJson { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? AgentId { get; set; }
    public string Target { get; set; } = "docker-script";
    [StringLength(100)] public string? Service { get; set; }
    [StringLength(Stanza.Core.Automation.BashScriptRules.MaximumLength)] public string? Script { get; set; }
    [StringLength(1000)] public string? Confirmation { get; set; }
    public bool ReplaceLegacy { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public bool IsLegacy { get; set; }

    public static bool IsScript(ActionPlan plan) => plan.Steps is { Count: 1 } &&
        plan.Steps[0].Kind is "docker-script" or "container-script";

    public static ActionEditorModel From(ActionDefinition definition, ActionPlan plan) => new()
    {
        Id = definition.Id, Revision = definition.Revision, Name = definition.Name, Enabled = definition.Enabled,
        Concurrency = definition.Concurrency, DeploymentId = plan.DeploymentId, Confirmation = plan.Confirmation,
        AgentId = IsScript(plan) ? plan.Steps[0].AgentId : null,
        IsLegacy = !IsScript(plan),
        Target = IsScript(plan) ? plan.Steps[0].Kind : "docker-script",
        Script = IsScript(plan) ? plan.Steps[0].Execution?.Script : null,
        Service = IsScript(plan) ? plan.Steps[0].Execution?.Service : null,
        PlanJson = IsScript(plan) ? null : System.Text.Json.JsonSerializer.Serialize(plan, AutomationRules.Json)
    };

    public ActionPlan ToPlan(ActionPlan? existing = null)
    {
        // Keep explicit legacy API submissions compatible; the script editor never posts plan JSON.
        if (!string.IsNullOrEmpty(PlanJson) && string.IsNullOrEmpty(Script)) return AutomationRules.ParsePlan(PlanJson);
        if (existing is not null && !IsScript(existing) && !ReplaceLegacy)
            throw new AutomationException("Confirm replacing the existing action with a script. Its steps and inputs will be replaced.");
        if (DeploymentId == Guid.Empty || AgentId == Guid.Empty || (DeploymentId.HasValue == AgentId.HasValue) || Target == "container-script" && AgentId.HasValue)
            throw new AutomationException("Select one deployment, or one Agent for a Host Docker action.");
        var body = Script;
        if (existing is not null && IsScript(existing) && existing.Steps[0].Execution?.Script is { } original &&
            body is not null && NormalizeLines(body) == NormalizeLines(original)) body = original;
        var execution = new Stanza.Core.Contracts.AgentExecution("/bin/bash", [],
            Service: Target == "container-script" ? Service : null, Mode: "script", Script: body);
        AutomationRules.ValidateScript(Target, execution);
        var step = new ActionStep(Target, DeploymentId, AgentId, execution);
        var plan = existing is not null
            ? existing with { Steps = [step], Parameters = null, DeploymentId = DeploymentId, Confirmation = Confirmation }
            : new ActionPlan([step], DeploymentId: DeploymentId, Confirmation: Confirmation);
        AutomationRules.Validate(plan);
        return plan;
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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
