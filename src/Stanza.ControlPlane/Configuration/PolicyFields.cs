namespace Stanza.ControlPlane.Configuration;

public sealed record PolicyField(string Name, string Label, string Help);
public sealed record PolicyGroup(string Id, string Title, PolicyField[] Fields);

public static class PolicyFields
{
    public static IReadOnlyList<PolicyGroup> Groups { get; } =
    [
        new("operations", "Operations", [
            new(nameof(OperationalPolicy.OperationTimeoutSeconds), "Operation timeout (seconds)", "Global deadline for the whole operation on every Agent. Default: 3,600 seconds (1 hour). 0: no timeout. Cancellation cannot undo changes already made."),
            new(nameof(OperationalPolicy.MaximumParallelOperations), "Concurrent operations", "Maximum operations running across the instance. Each Agent still runs one mutating operation at a time. Default: 4. 0: unlimited."),
            new(nameof(OperationalPolicy.AllowHostActions), "Allow host actions", "Permit authorized host commands. Also requires an explicitly privileged Agent deployment; this switch cannot grant host permissions.")]),
        new("deployment-files", "Deployment files", [
            new(nameof(OperationalPolicy.MaximumTextBytes), "Editable text per file (bytes)", "Compose, environment, and supporting text files. Default: 4 MiB (4,194,304 bytes). 0: unlimited. Large text increases editor and diff costs."),
            new(nameof(OperationalPolicy.MaximumUploadBytes), "Uploaded file size (bytes)", "Maximum individual managed file. Default: 16 MiB (16,777,216 bytes). 0: unlimited."),
            new(nameof(OperationalPolicy.MaximumWorkspaceBytes), "Total managed-file size (bytes)", "Total files saved in a deployment revision, not Docker images, volumes, or Git checkouts. Default: 64 MiB (67,108,864 bytes). 0: unlimited. Complete revisions consume database storage."),
            new(nameof(OperationalPolicy.MaximumSupportingFiles), "Supporting files and directories", "Entries in addition to Compose and deployment environment files. Default: 128. 0: unlimited.")]),
        new("security", "Security and access", [
            new(nameof(OperationalPolicy.SessionHours), "Session duration (hours)", "Applies to new sign-ins. Default: 8 hours. 0: no scheduled expiry; account disablement and access revocation still apply."),
            new(nameof(OperationalPolicy.LoginAttempts), "Login attempts per client IP", "Attempts permitted within the login window. Default: 20. 0: no rate limit."),
            new(nameof(OperationalPolicy.LoginWindowMinutes), "Login rate-limit window (minutes)", "Time before the attempt allowance resets. Default: 5 minutes. 0: an unbounded window (no automatic reset until restart or policy change)."),
            new(nameof(OperationalPolicy.EnrollmentRequestsPerMinute), "Agent enrollment requests per minute", "Per-client-IP enrollment request allowance. Default: 120. 0: unlimited.")]),
        new("networking", "Networking and trusted proxies", [
            new(nameof(OperationalPolicy.TrustedProxyAddresses), "Trusted proxy addresses and networks", "Comma/newline-separated IPs or CIDRs. New installations trust loopback and private IPv4/IPv6 networks. Empty: trust none. Only trust networks whose clients cannot bypass your proxy; forwarded headers affect client IP and scheme."),
            new(nameof(OperationalPolicy.TrustedProxyForwardLimit), "Forwarded proxy hops", "Maximum forwarded-header entries processed from trusted proxies. Default: 1. 0: unlimited hops, not trust-all addresses.")]),
        new("live-updates", "Live updates and connections", [
            new(nameof(OperationalPolicy.StatusIntervalSeconds), "Status refresh interval (seconds)", "Refresh the dashboard status feed. Default: 15 seconds. 0: initial status only, no periodic refresh. Operation output streams independently."),
            new(nameof(OperationalPolicy.ConfigurationIntervalSeconds), "Agent configuration check interval (seconds)", "Check for saved configuration changes. Default: 15 seconds. 0: synchronize only on connection/reconnection; reconnect Agents to resume checks after changing this from 0."),
            new(nameof(OperationalPolicy.StaleStatusSeconds), "Observation freshness (seconds)", "Age after which a recorded deployment observation is stale. Default: 15 seconds. 0: no age-based expiry. Offline and never-observed states remain explicit."),
            new(nameof(OperationalPolicy.MaximumConnectionsPerUser), "Live connections per user", "Concurrent status, output, logs, and terminal streams per user. Default: 4. 0: unlimited."),
            new(nameof(OperationalPolicy.MaximumConnections), "Live connections per instance", "Total concurrent browser streams. Default: 1,024. 0: unlimited.")]),
        new("retention", "Retention", [
            new(nameof(OperationalPolicy.LogRetentionDays), "Operation output retention (days)", "Delete output after completed operations reach this age; retain operation metadata. Default: 30 days. 0: retain forever. Output has no character limit, so monitor database disk space."),
            new(nameof(OperationalPolicy.AuditRetentionDays), "Audit retention (days)", "Delete older audit records. Default: 365 days. 0: retain forever.")])
    ];
}
