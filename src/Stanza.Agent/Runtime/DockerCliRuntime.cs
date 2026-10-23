using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace Stanza.Agent.Runtime;

public sealed class DockerCliRuntime(
    IProcessRunner runner,
    IOptions<AgentOptions> agentOptions,
    IOptions<DockerRuntimeOptions> runtimeOptions) : IContainerRuntime
{
    private readonly SemaphoreSlim execution = new(1, 1);

    private (WorkspacePolicy Workspace, string Home) PrepareStorage()
    {
        var root = PrivateStorage.EnsureDirectory(agentOptions.Value.PersistentDataRoot);
        var home = PrivateStorage.EnsureDirectory(Path.Combine(root, "runtime-home"));
        // No ambient ~/.docker credentials, CLI plugins, contexts or configuration.
        PrivateStorage.EnsureDirectory(Path.Combine(home, ".docker"));
        return (new WorkspacePolicy(root), home);
    }

    private ProcessRequest CreateProcess(string directory, string home, IReadOnlyList<string> arguments, bool probe = false) =>
        new(runtimeOptions.Value.ExecutablePath, directory, arguments, new Dictionary<string, string>
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = home,
            ["DOCKER_CONFIG"] = Path.Combine(home, ".docker"),
            ["DOCKER_HOST"] = runtimeOptions.Value.Endpoint,
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8"
        }, Stanza.Core.Contracts.LimitPolicy.Duration(probe ? 15 : runtimeOptions.Value.OperationTimeoutSeconds));

    public async Task<RuntimeCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (!DockerRuntimeOptions.IsValid(runtimeOptions.Value)) { return Capabilities(false, false); }
        try
        {
            var (workspace, home) = PrepareStorage();
            // Query the server, not merely the CLI binary, to detect an unavailable daemon.
            var docker = await runner.RunAsync(CreateProcess(workspace.Root, home,
                ["version", "--format", "{{.Server.Version}}"], true), cancellationToken);
            var dockerAvailable = Successful(docker) && ValidVersion(docker.StandardOutput);
            var compose = await runner.RunAsync(CreateProcess(workspace.Root, home,
                ["compose", "version", "--short"], true), cancellationToken);
            var composeAvailable = Successful(compose) && ValidVersion(compose.StandardOutput);
            return Capabilities(dockerAvailable, composeAvailable,
                dockerAvailable ? docker.StandardOutput.Trim() : null, composeAvailable ? compose.StandardOutput.Trim() : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Capabilities(false, false);
        }
    }

    public async Task<RuntimeResult> ExecuteAsync(ComposeRequest request, CancellationToken cancellationToken = default)
    {
        if (!runtimeOptions.Value.EnableExecution) { return new(false, "execution_disabled"); }
        if (!DockerRuntimeOptions.IsValid(runtimeOptions.Value)) { return new(false, "invalid_runtime_configuration"); }
        if (!Enum.IsDefined(request.Operation) || !WorkspacePolicy.IsIdentifier(request.ProjectName) ||
            (request.Services?.Any(service => !WorkspacePolicy.IsIdentifier(service)) ?? false))
        {
            return new(false, "invalid_compose_request");
        }

        await execution.WaitAsync(cancellationToken);
        try
        {
            var (workspace, home) = PrepareStorage();
            var directory = workspace.GetWorkspace(request.WorkspaceId);
            var compose = WorkspacePolicy.GetFile(directory, request.ComposeFile);
            var environment = WorkspacePolicy.GetFile(directory, request.EnvironmentFile);
            var arguments = new List<string>
            {
                "compose", "--ansi", "never", "--project-directory", directory,
                "--project-name", request.ProjectName, "--env-file", environment, "--file", compose
            };
            arguments.AddRange(request.Operation switch
            {
                ComposeOperation.Validate => ["config", "--quiet"],
                ComposeOperation.Build => ["build"],
                ComposeOperation.Pull => ["pull"],
                ComposeOperation.Up => ["up", "--detach", "--no-build", "--pull", "never"],
                ComposeOperation.Down => ["down"], // Deliberately never --volumes or --rmi.
                ComposeOperation.Start => ["start"],
                ComposeOperation.Stop => ["stop"],
                ComposeOperation.Restart => ["restart"],
                ComposeOperation.Ps => ["ps", "--all", "--format", "json"],
                ComposeOperation.Logs => ["logs", "--no-color", "--timestamps", "--tail", "200"],
                _ => throw new InvalidOperationException("Unsupported Compose operation.")
            });
            if (request.Services is { Count: > 0 })
            {
                if (request.Operation is ComposeOperation.Validate or ComposeOperation.Down)
                {
                    return new(false, "service_selection_unavailable");
                }
                arguments.AddRange(request.Services);
            }

            var result = await runner.RunAsync(CreateProcess(directory, home, arguments), cancellationToken);
            if (result.TimedOut) { return new(false, "runtime_timeout"); }
            if (result.OutputLimitExceeded) { return new(false, "runtime_output_limit"); }
            if (result.ExitCode != 0) { return new(false, "compose_failed"); }
            return new(true, "succeeded", request.Operation is ComposeOperation.Logs or ComposeOperation.Ps ? result.StandardOutput : null);
        }
        catch (ArgumentException) { return new(false, "invalid_workspace"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new(false, "runtime_unavailable");
        }
        finally { execution.Release(); }
    }

    private static bool Successful(ProcessResult result) => result.ExitCode == 0 && !result.TimedOut && !result.OutputLimitExceeded;
    private static bool ValidVersion(string version)
    {
        var value = version.Trim();
        return value.Length is > 0 and <= 64 && char.IsAsciiDigit(value.TrimStart('v').FirstOrDefault()) &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');
    }
    private RuntimeCapabilities Capabilities(bool docker, bool compose, string? dockerVersion = null, string? composeVersion = null) =>
        new("docker", docker, compose, dockerVersion, composeVersion, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            runtimeOptions.Value.EnableExecution && docker && compose);
}
