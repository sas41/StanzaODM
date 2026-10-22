using System.Text;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class DockerIntegrationFactAttribute : FactAttribute
{
    public DockerIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STANZA_TEST_DOCKER") != "1")
            Skip = "Set STANZA_TEST_DOCKER=1 to run isolated real Docker/Compose lifecycle verification.";
    }
}

public sealed class AgentDockerIntegrationTests
{
    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task RealComposeBuildDeployHealthExecLifecycleAndCleanupUseOnlyDedicatedProject()
    {
        using var root = new AgentTestDirectory();
        var runner = new ProcessRunner(TimeProvider.System);
        var deploymentId = Guid.NewGuid();
        var project = "stanza-" + deploymentId.ToString("N");
        var image = "stanza-transport-test-" + deploymentId.ToString("N");
        var executor = new AgentWorkExecutor(runner, Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        var files = new AgentFile[]
        {
            new("compose.yaml", Encoding.UTF8.GetBytes($$"""
                services:
                  app:
                    image: {{image}}
                    build: .
                    environment:
                      STANZA_LAYER: ${STANZA_LAYER}
                    healthcheck:
                      test: ["CMD", "test", "-e", "/etc/os-release"]
                      interval: 1s
                      timeout: 1s
                      retries: 10
                """)),
            new("Dockerfile", Encoding.UTF8.GetBytes("FROM ubuntu:24.04\nCMD [\"/bin/sh\",\"-c\",\"echo stanza-transport-ready; exec sleep 300\"]\n")),
            // An implicit workspace .env must never override the explicitly resolved scopes.
            new(".env", Encoding.UTF8.GetBytes("STANZA_LAYER=implicit-unsafe\n"))
        };
        AgentWorkRequest Request(string operation, AgentExecution? command = null) => new(Guid.NewGuid(), deploymentId, operation, files,
            [new("Global", "STANZA_LAYER=global\n"), new("Agent", "STANZA_LAYER=agent\n"), new("Deployment", "STANZA_LAYER=deployment\n")],
            command, Policy: new(TimeoutSeconds: 120, MaximumOutputCharacters: 1048576));
        var requests = new List<AgentWorkRequest>();
        async Task<AgentWorkResponse> Run(string operation, AgentExecution? command = null)
        {
            var request = Request(operation, command); requests.Add(request);
            var result = await executor.ExecuteAsync(request);
            Assert.True(result.Succeeded, operation + ": " + result.Code);
            return result;
        }
        try
        {
            await Run("validate");
            await Run("build");
            await Run("deploy");
            var status = await Run("status");
            Assert.Contains("healthy", status.Output);
            var echo = await Run("container-exec", new("/usr/bin/printf", ["%s", "literal;$(not-shell)"], Service: "app"));
            Assert.Equal("literal;$(not-shell)", echo.Output);
            var environment = await Run("container-exec", new("/usr/bin/printenv", ["STANZA_LAYER"], Service: "app"));
            Assert.Equal("deployment", environment.Output!.Trim());
            Assert.Contains("stanza-transport-ready", (await Run("logs")).Output);
            await Run("restart"); await Run("stop"); await Run("start");
            var deployment = requests.Single(x => x.Operation == "deploy");
            Assert.True((await executor.ExecuteAsync(deployment)).Succeeded); // Durable completed replay, not a second deploy.
        }
        finally
        {
            // No global prune, no volume deletion, and no access to any existing user's Compose project.
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["compose", "--project-name", project,
                "--file", WriteCleanupCompose(root.Path, image), "down", "--remove-orphans"],
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(30)));
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["image", "rm", "--", image],
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(30)));
        }
    }

    private static string WriteCleanupCompose(string directory, string image)
    {
        var path = Path.Combine(directory, "cleanup.yaml");
        File.WriteAllText(path, "services:\n  app:\n    image: " + image + "\n");
        return path;
    }
}
