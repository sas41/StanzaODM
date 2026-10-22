namespace Stanza.Agent.Runtime;

public enum ComposeOperation { Validate, Build, Pull, Up, Down, Start, Stop, Restart, Ps, Logs }

public sealed record RuntimeCapabilities(string Runtime, bool DockerAvailable, bool ComposeAvailable,
    string? DockerVersion, string? ComposeVersion, string Architecture, bool ExecutionEnabled)
{
    public bool CommandChannelAvailable => false;
    public bool HostExecutionAvailable => false;
    public bool TerminalAvailable => false;
}

// WorkspaceId and ComposeFile are managed relative paths, never arbitrary host paths.
// All files must be an immutable, trusted operation snapshot. This adapter is NOT a
// sandbox for untrusted Compose: Docker socket access grants substantial host control.
public sealed record ComposeRequest(ComposeOperation Operation, string WorkspaceId, string ComposeFile,
    string ProjectName, string EnvironmentFile, IReadOnlyList<string>? Services = null);

public sealed record RuntimeResult(bool Succeeded, string Code, string? SensitiveOutput = null)
{
    // Logs and ps output may contain workload secrets and require explicit authorization.
    public override string ToString() => $"RuntimeResult {{ Succeeded = {Succeeded}, Code = {Code} }}";
}

public interface IContainerRuntime
{
    Task<RuntimeCapabilities> DetectAsync(CancellationToken cancellationToken = default);
    Task<RuntimeResult> ExecuteAsync(ComposeRequest request, CancellationToken cancellationToken = default);
}
