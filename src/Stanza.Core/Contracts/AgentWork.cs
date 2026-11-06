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
    IReadOnlyDictionary<string, string>? Environment = null, string Mode = "exec",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Script = null)
{
    public override string ToString() => "AgentExecution { [REDACTED] }";
}
public sealed record AgentRuntimePolicy(bool ExecutionEnabled = true, int TimeoutSeconds = 3600,
    string DockerEndpoint = "unix:///var/run/docker.sock");
public sealed record AgentOutputChunk(long Sequence, string Stream, string Text)
{
    public override string ToString() => $"AgentOutputChunk {{ Sequence = {Sequence}, Stream = {Stream}, Text = [REDACTED] }}";
}
public sealed record AgentJobInspection(Guid Id, bool Found, bool Active, bool Stopped, string Phase,
    bool ApplyStarted, long LastSequence, long AcknowledgedSequence, long? LastHeartbeat,
    string? CancellationReason = null, AgentWorkResponse? Response = null, bool OutputArchived = false);
public sealed record AgentOutputRead(Guid OperationId, long After, int Count = 64);
public sealed record AgentCapabilities(string Runtime, bool DockerAvailable, bool ComposeAvailable,
    string? DockerVersion, string? ComposeVersion, string Architecture, bool HostExecutionAvailable, bool LogsAvailable);
public static class AgentLogProtocol
{
    public const int MaximumChunk = 65536;
    public static bool IsTargetName(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
    public static bool IsValid(AgentExecution? value) => value is { Mode: "logs", Executable: "logs", Arguments.Count: 0,
        Script: null, User: null, WorkingDirectory: null, Environment: null } &&
        ((IsTargetName(value.Service) && value.Container is null) || (value.Service is null && IsTargetName(value.Container)));
}
public sealed record AgentImageObservation(string Service, string Image, string Platform, string? CurrentDigest,
    string RemoteDigest, bool LocallyBuilt = false, bool ExplicitRegistrySource = false);
public sealed record AgentWorkRequest(Guid Id, Guid DeploymentId, string Operation,
    IReadOnlyList<AgentFile> Files, IReadOnlyList<AgentEnvironmentFile> EnvironmentFiles,
    AgentExecution? Execution = null, IReadOnlyList<AgentGitSource>? Sources = null,
    IReadOnlyList<AgentRegistryCredential>? Registries = null, AgentRuntimePolicy? Policy = null,
    IReadOnlyDictionary<string, string>? ImageOverrides = null, string? ImageRepository = null,
    IReadOnlyDictionary<string, string>? SourceOverrides = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool IncludeGitMetadata = false)
{
    public override string ToString() => $"AgentWorkRequest {{ Id = {Id}, DeploymentId = {DeploymentId}, Operation = {Operation}, Contents = [REDACTED] }}";
}
public sealed record AgentWorkResponse(Guid Id, bool Succeeded, string Code, string? Output = null,
    IReadOnlyDictionary<string, string>? ResolvedSources = null, IReadOnlyDictionary<string, string>? ResolvedImages = null,
    IReadOnlyList<GitReference>? GitReferences = null, bool ExecutionStopped = false, string? CancellationReason = null,
    bool ApplyStarted = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool OutputIncomplete = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] long LastOutputSequence = 0)
{
    public override string ToString() => $"AgentWorkResponse {{ Id = {Id}, Succeeded = {Succeeded}, Code = {Code}, Output = [REDACTED] }}";
}
public sealed record GitReference(string Name, string? Commit = null, long? CommittedAt = null, string? Author = null)
{
    public string Label => Name + (Commit is null ? "" : " · " + Commit[..Math.Min(8, Commit.Length)]) +
        (CommittedAt is { } date ? " · " + DateTimeOffset.FromUnixTimeSeconds(date).ToString("u") : "") +
        (string.IsNullOrEmpty(Author) ? "" : " · " + Author);
}
public sealed record AgentWireMessage(string Type, Guid Id, AgentWorkRequest? Request = null,
    AgentWorkResponse? Response = null, string? Data = null,
    AgentConfiguration? Configuration = null, AgentDeploymentConfiguration? Deployment = null,
    AgentJobInspection? Job = null,
    AgentOutputRead? OutputRead = null, IReadOnlyList<AgentOutputChunk>? OutputBatch = null)
{
    public const int Version = 7;
    public override string ToString() => $"AgentWireMessage {{ Type = {Type}, Id = {Id}, Contents = [REDACTED] }}";
}
