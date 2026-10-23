namespace Stanza.Core.Security;

public sealed record PermissionDefinition(string Action, string ResourceType, string Description);

/// <summary>
/// Only these exact action/resource pairs may be delegated. Account, group and role administration
/// are deliberately not delegable. Null resource IDs cover all resources of the specified type;
/// neither actions nor resource types support wildcard strings.
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> All { get; } = Array.AsReadOnly<PermissionDefinition>(
    [
        new("instance.read", "instance", "View the instance dashboard"),
        new("settings.read", "instance", "View instance settings"),
        new("settings.write", "instance", "Change operational instance settings, not account/security policy"),
        new("agent.read", "agent", "View an Agent"),
        new("agent.manage", "agent", "Manage an Agent"),
        new("deployment.create", "instance", "Create deployments"),
        new("deployment.read", "deployment", "View a deployment"),
        new("deployment.validate", "deployment", "Validate a deployment"),
        new("deployment.build", "deployment", "Build deployment images"),
        new("deployment.deploy", "deployment", "Deploy or redeploy a deployment"),
        new("deployment.start", "deployment", "Start a deployment"),
        new("deployment.stop", "deployment", "Stop a deployment"),
        new("deployment.restart", "deployment", "Restart a deployment"),
        new("deployment.remove", "deployment", "Remove a deployment"),
        new("files.read", "deployment", "Read managed deployment files"),
        new("files.write", "deployment", "Edit managed deployment files"),
        new("logs.read", "deployment", "Read deployment logs"),
        new("terminal.open", "deployment", "Open a container terminal"),
        new("container.execute", "deployment", "Execute a configured command in deployment containers"),
        new("sources.manage", "deployment", "Manage deployment Git and registry source references"),
        new("deployment.clone", "deployment", "Clone a deployment configuration"),
        new("deployment.move", "deployment", "Move a deployment to another Agent"),
        new("operations.read", "deployment", "Read deployment operation history"),
        new("operations.cancel", "deployment", "Cancel a deployment operation"),
        new("audit.read", "instance", "Read redacted audit events"),
        new("host.execute", "agent", "Execute an explicitly configured host action"),
        new("environment.read", "instance", "View global environment metadata"),
        new("environment.write", "instance", "Edit global environment values"),
        new("environment.reveal", "instance", "Reveal global environment values"),
        new("environment.read", "agent", "View Agent environment metadata"),
        new("environment.write", "agent", "Edit Agent environment values"),
        new("environment.reveal", "agent", "Reveal Agent environment values"),
        new("environment.read", "deployment", "View deployment environment metadata"),
        new("environment.write", "deployment", "Edit deployment environment values"),
        new("environment.reveal", "deployment", "Reveal deployment environment values"),
        new("credential.read", "credential", "View credential metadata"),
        new("credential.write", "credential", "Change credentials"),
        new("credential.use", "credential", "Use credentials without revealing them"),
        new("credential.reveal", "credential", "Reveal credential values"),
        new("action.read", "action", "View a configured action"),
        new("action.manage", "action", "Manage a configured action"),
        new("action.execute", "action", "Execute only this fixed action using its owner's approved target capabilities"),
        new("automation.read", "deployment", "View deployment automation"),
        new("automation.manage", "deployment", "Manage deployment automation"),
        new("configuration.export", "instance", "Export instance configuration; included resources require separate authorization"),
        new("configuration.import", "instance", "Import operational configuration, never account/security policy"),
        new("configuration.export", "deployment", "Export deployment configuration; secrets require separate authorization"),
        new("configuration.import", "deployment", "Import deployment configuration")
    ]);

    public static bool Contains(string action, string resourceType) =>
        action is { Length: > 0 and <= 100 } && resourceType is { Length: > 0 and <= 100 }
        && All.Any(definition => definition.Action == action && definition.ResourceType == resourceType);
}
