using System.Text.Json;
using System.Text.RegularExpressions;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Automation;

public sealed class AutomationException(string message) : Exception(message);
public sealed class AutomationAccessException() : Exception("Current access does not permit this automation or one of its targets.");

public sealed record ActionParameter(string Name, string Type = "string", bool Required = true, string? Default = null);
public sealed record ActionStep(string Kind, Guid? DeploymentId = null, Guid? AgentId = null,
    AgentExecution? Execution = null, int TimeoutSeconds = 300, string Failure = "stop");
public sealed record ActionPlan(IReadOnlyList<ActionStep> Steps, IReadOnlyList<ActionParameter>? Parameters = null)
{
    public override string ToString() => "ActionPlan { [REDACTED] }";
}
public sealed record AutomationLimits(int MaximumWebhookBytes = 65536, int DeliveriesPerMinute = 60,
    int HistoryRetentionDays = 30, int MaximumQueuedRuns = 1000);

public static partial class AutomationRules
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, MaxDepth = 24 };
    public static readonly string[] Operations = ["git-refresh", "validate", "build", "pull", "deploy", "start", "stop", "restart", "remove", "status", "logs"];
    // Git refresh uses the existing safe source-preparation/Compose-validation operation, not
    // an ad-hoc Git process or a shared mutable checkout in the Control Plane.
    public static string OperationKind(string stepKind) => stepKind == "git-refresh" ? "validate" : stepKind;
    public static string DescribeCron(string expression)
    {
        try { return new Stanza.Core.Automation.CronSchedule(expression).Describe(); }
        catch (ArgumentException) { return "Invalid schedule; edit the expression before enabling."; }
    }
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();

    public static string Name(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && !name.Any(char.IsControl)
        ? name.Trim() : throw new AutomationException("Use a name of 1–100 characters without control characters.");

    public static ActionPlan ParsePlan(string json)
    {
        if (json.Length > 131072) throw new AutomationException("Action JSON must be at most 128 KiB.");
        try
        {
            var plan = JsonSerializer.Deserialize<ActionPlan>(json, Json) ?? throw new AutomationException("Supply an action plan.");
            Validate(plan); return plan;
        }
        catch (JsonException) { throw new AutomationException("Invalid action JSON."); }
    }

    public static void Validate(ActionPlan plan)
    {
        if (plan.Steps is null || plan.Steps.Count is < 1 or > 50 || (plan.Parameters?.Count ?? 0) > 50)
            throw new AutomationException("Use 1–50 steps and at most 50 parameters.");
        var parameters = plan.Parameters ?? [];
        if (parameters.Any(p => p is null || p.Name is null || !NamePattern().IsMatch(p.Name) || p.Type is not ("string" or "integer" or "boolean")))
            throw new AutomationException("Parameters require unique identifier names and string, integer, or boolean types.");
        if (parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
            throw new AutomationException("Duplicate parameter names.");
        foreach (var p in parameters)
        {
            if (!p.Required && p.Type != "string" && p.Default is null)
                throw new AutomationException("Optional integer/boolean parameters require a typed default.");
            if (p.Default is not null) ValidateValue(p, p.Default);
        }
        foreach (var step in plan.Steps)
        {
            if (step is null || step.TimeoutSeconds is < 1 or > 3600 || step.Failure is not ("stop" or "continue"))
                throw new AutomationException("Each step needs a 1–3600 second timeout and stop/continue failure policy.");
            if (step.Kind == "host-exec")
            {
                if (step.AgentId is null || step.AgentId == Guid.Empty || step.DeploymentId is not null)
                    throw new AutomationException("Host execution requires an explicit Agent and no deployment target.");
            }
            else if ((!Operations.Contains(step.Kind) && step.Kind != "container-exec") || step.DeploymentId is null || step.DeploymentId == Guid.Empty || step.AgentId is not null)
                throw new AutomationException("Select a supported operation and an explicit deployment target.");
            if (step.Kind is "container-exec" or "host-exec")
            {
                var execution = step.Execution;
                if (execution is null || string.IsNullOrWhiteSpace(execution.Executable) || execution.Executable.Length > 1024 || execution.Executable.Contains('\0') || execution.Executable.Contains("${", StringComparison.Ordinal))
                    throw new AutomationException("Specify a fixed executable; variables are allowed only as whole structured arguments.");
                if (step.Kind == "container-exec" && (string.IsNullOrWhiteSpace(execution.Service) == string.IsNullOrWhiteSpace(execution.Container)))
                    throw new AutomationException("Container execution requires exactly one service or container; missing/stopped targets fail, never auto-start.");
                if (step.Kind == "host-exec" && (execution.Service is not null || execution.Container is not null))
                    throw new AutomationException("Host execution cannot select a container.");
                if (execution.Arguments is null || execution.Arguments.Count > 128 || execution.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
                    throw new AutomationException("Use at most 128 bounded, NUL-free arguments.");
                foreach (var argument in execution.Arguments)
                {
                    if (!argument.Contains("${", StringComparison.Ordinal)) continue;
                    if (!argument.StartsWith("${", StringComparison.Ordinal) || !argument.EndsWith('}') || parameters.All(p => argument != "${" + p.Name + "}"))
                        throw new AutomationException("A variable must occupy a whole argument and reference a declared parameter.");
                    // Shells intentionally authored by the editor are supported, but never accept external substitutions.
                    if (IsInterpreter(execution.Executable))
                        throw new AutomationException("External parameters cannot be passed to script interpreters; use a fixed program with structured arguments.");
                }
                var fixedValues = new[] { execution.Service, execution.Container, execution.User, execution.WorkingDirectory }
                    .Concat(execution.Environment?.SelectMany(p => new[] { p.Key, p.Value }) ?? []);
                if (fixedValues.Any(v => v is not null && (v.Length > 8192 || v.Contains('\0') || v.Contains("${", StringComparison.Ordinal))) || execution.Environment?.Count > 100)
                    throw new AutomationException("Target, user, directory and environment must be bounded fixed values, not parameter mappings.");
            }
            else if (step.Execution is not null) throw new AutomationException("Only execution steps accept command fields.");
        }
    }

    private static bool IsInterpreter(string executable) => new[] { "sh", "bash", "dash", "zsh", "ash", "fish", "cmd", "cmd.exe", "powershell", "pwsh", "python", "python3", "node", "perl", "ruby", "env" }
        .Contains(executable.Split('/').Last(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Parameters(ActionPlan plan, IReadOnlyDictionary<string, string>? supplied)
    {
        var definitions = plan.Parameters ?? [];
        if (supplied is not null && supplied.Keys.Any(k => definitions.All(p => p.Name != k))) throw new AutomationException("Unknown action parameter.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in definitions)
        {
            var value = supplied?.GetValueOrDefault(p.Name) ?? p.Default;
            if (value is null && p.Required) throw new AutomationException("A required action parameter is missing.");
            value ??= ""; ValidateValue(p, value); values.Add(p.Name, value);
        }
        return values;
    }

    public static ActionStep Resolve(ActionStep step, IReadOnlyDictionary<string, string> values) => step.Execution is null ? step : step with
    {
        Execution = step.Execution with { Arguments = step.Execution.Arguments.Select(a =>
            a.StartsWith("${", StringComparison.Ordinal) && a.EndsWith('}') ? values[a[2..^1]] : a).ToArray() }
    };

    private static void ValidateValue(ActionParameter p, string value)
    {
        if (value.Length > 4096 || value.Contains('\0') || (p.Type == "integer" && !long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
            || (p.Type == "boolean" && value is not ("true" or "false"))) throw new AutomationException("An action parameter has an invalid type or size.");
    }

    public static void ValidateLimits(AutomationLimits limits)
    {
        if (limits.MaximumWebhookBytes is < 1024 or > 1048576 || limits.DeliveriesPerMinute is < 1 or > 10000
            || limits.HistoryRetentionDays is < 1 or > 3650 || limits.MaximumQueuedRuns is < 1 or > 100000)
            throw new AutomationException("Automation limits are outside the supported ranges.");
    }
}
