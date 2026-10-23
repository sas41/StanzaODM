namespace Stanza.Core.Contracts;

public sealed record AgentFile(string Path, byte[] Content, bool IsDirectory = false)
{
    public override string ToString() => $"AgentFile {{ Path = {Path}, Bytes = {Content.Length} }}";
}
public sealed record AgentEnvironmentFile(string Name, string Text)
{
    public override string ToString() => $"AgentEnvironmentFile {{ Name = {Name}, Text = [REDACTED] }}";
}
public sealed record AgentGitSource(string Url, string Reference, string Destination, string? Username = null, string? Password = null)
{
    public override string ToString() => "AgentGitSource { [REDACTED] }";
}
public sealed record AgentRegistryCredential(string Server, string Username, string Password)
{
    public override string ToString() => "AgentRegistryCredential { [REDACTED] }";
}
public sealed record AgentExecution(string Executable, IReadOnlyList<string> Arguments, string? Service = null,
    string? Container = null, string? User = null, string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null, string Mode = "exec")
{
    public override string ToString() => "AgentExecution { [REDACTED] }";
}
public sealed record AgentRuntimePolicy(bool ExecutionEnabled = true, int TimeoutSeconds = 300,
    int MaximumOutputCharacters = 65536, string DockerEndpoint = "unix:///var/run/docker.sock");
public sealed record AgentCapabilities(string Runtime, bool DockerAvailable, bool ComposeAvailable,
    string? DockerVersion, string? ComposeVersion, string Architecture, bool HostExecutionAvailable, bool TerminalAvailable);
public sealed record AgentImageObservation(string Service, string Image, string Platform, string? CurrentDigest,
    string RemoteDigest, bool LocallyBuilt = false, bool ExplicitRegistrySource = false);
public sealed record AgentWorkRequest(Guid Id, Guid DeploymentId, string Operation,
    IReadOnlyList<AgentFile> Files, IReadOnlyList<AgentEnvironmentFile> EnvironmentFiles,
    AgentExecution? Execution = null, IReadOnlyList<AgentGitSource>? Sources = null,
    IReadOnlyList<AgentRegistryCredential>? Registries = null, AgentRuntimePolicy? Policy = null,
    IReadOnlyDictionary<string, string>? ImageOverrides = null, string? ImageRepository = null,
    IReadOnlyDictionary<string, string>? SourceOverrides = null)
{
    public override string ToString() => $"AgentWorkRequest {{ Id = {Id}, DeploymentId = {DeploymentId}, Operation = {Operation}, Contents = [REDACTED] }}";
}
public sealed record AgentWorkResponse(Guid Id, bool Succeeded, string Code, string? Output = null,
    IReadOnlyDictionary<string, string>? ResolvedSources = null, IReadOnlyDictionary<string, string>? ResolvedImages = null)
{
    public override string ToString() => $"AgentWorkResponse {{ Id = {Id}, Succeeded = {Succeeded}, Code = {Code}, Output = [REDACTED] }}";
}
public sealed record AgentWireMessage(string Type, Guid Id, AgentWorkRequest? Request = null,
    AgentWorkResponse? Response = null, string? Data = null,
    AgentConfiguration? Configuration = null, AgentDeploymentConfiguration? Deployment = null)
{
    public const int Version = 2;
    public override string ToString() => $"AgentWireMessage {{ Type = {Type}, Id = {Id}, Contents = [REDACTED] }}";
}
