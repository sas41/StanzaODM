using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Stanza.Core.Contracts;

namespace Stanza.Agent.Runtime;

public interface IDockerCapabilitiesProbe
{
    Task<AgentCapabilities> DetectAsync(CancellationToken cancellationToken = default);
}

public sealed class DockerCapabilitiesProbe(IProcessRunner runner, IOptions<AgentOptions> agent,
    IOptions<DockerRuntimeOptions> docker) : IDockerCapabilitiesProbe
{
    public async Task<AgentCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? dockerVersion = null, composeVersion = null;
        if (DockerRuntimeOptions.IsValid(docker.Value))
        {
            try
            {
                var home = PrivateStorage.EnsureDirectory(Path.Combine(agent.Value.PersistentDataRoot, "runtime-home"));
                var configuration = PrivateStorage.EnsureDirectory(Path.Combine(home, ".docker"));
                var environment = new Dictionary<string, string> { ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                    ["HOME"] = home, ["DOCKER_CONFIG"] = configuration, ["DOCKER_HOST"] = docker.Value.Endpoint, ["LANG"] = "C.UTF-8", ["LC_ALL"] = "C.UTF-8" };
                async Task<string?> Probe(params string[] arguments)
                {
                    var result = await runner.RunAsync(new(docker.Value.ExecutablePath, home, arguments, environment, TimeSpan.FromSeconds(15)), cancellationToken);
                    var version = result.StandardOutput.Trim();
                    return result.ExitCode == 0 && !result.TimedOut && !result.OutputLimitExceeded && version.Length is > 0 and <= 64 &&
                        char.IsAsciiDigit(version.TrimStart('v').FirstOrDefault()) &&
                        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version : null;
                }
                dockerVersion = await Probe("version", "--format", "{{.Server.Version}}");
                composeVersion = await Probe("compose", "version", "--short");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
        return new("docker", dockerVersion is not null, composeVersion is not null, dockerVersion, composeVersion,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), false, dockerVersion is not null && OperatingSystem.IsLinux());
    }
}
