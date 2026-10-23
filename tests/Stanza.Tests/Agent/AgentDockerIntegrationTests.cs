using System.Text;
using System.Text.Json;
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
    public async Task BuildOnlyRedeploymentReplacesBothReplicasWithTheNewBuiltIdAndKeepsInactiveServicesInactive()
    {
        using var root = new AgentTestDirectory();
        var runner = new ProcessRunner(TimeProvider.System); var deployment = Guid.NewGuid();
        var project = "stanza-" + deployment.ToString("N");
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" };
        var executor = new AgentWorkExecutor(runner, Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        const string compose = """
            name: authored-project-ignored-by-stanza
            services:
              app:
                build: .
                deploy:
                  replicas: 2
                healthcheck:
                  test: [CMD, test, -s, /release.txt]
                  interval: 1s
                  timeout: 1s
                  retries: 10
              zero:
                build: .
                deploy:
                  replicas: 0
              inactive:
                profiles: [manual]
                build: .
            """;
        AgentWorkRequest Request(string release) => new(Guid.NewGuid(), deployment, "deploy",
            [new("compose.yaml", Encoding.UTF8.GetBytes(compose)),
             new("Dockerfile", Encoding.UTF8.GetBytes("FROM ubuntu:24.04\nCOPY release.txt /release.txt\nCMD [\"/usr/bin/sleep\",\"300\"]\n")),
             new("release.txt", Encoding.UTF8.GetBytes(deployment.ToString("N") + ":" + release))],
            [new("Global", ""), new("Agent", ""), new("Deployment", "")], Policy: new(TimeoutSeconds: 180));
        Task<ProcessResult> Docker(params string[] args) => runner.RunAsync(new("/usr/bin/docker", root.Path, args, environment, TimeSpan.FromSeconds(30)));
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var first = await executor.ExecuteAsync(Request("first"));
            Assert.True(first.Succeeded, first.Code); imageIds.Add(first.ResolvedImages!["app:image-id"]);
            var second = await executor.ExecuteAsync(Request("second"));
            Assert.True(second.Succeeded, second.Code); imageIds.Add(second.ResolvedImages!["app:image-id"]);
            Assert.NotEqual(first.ResolvedImages["app:image-id"], second.ResolvedImages["app:image-id"]);
            Assert.DoesNotContain("inactive:image-id", second.ResolvedImages.Keys);
            var containers = await Docker("ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=" + project, "--filter", "label=com.docker.compose.service=app");
            Assert.Equal(0, containers.ExitCode);
            var ids = containers.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, ids.Length);
            foreach (var id in ids)
            {
                var actual = await Docker("inspect", "--format", "{{.Image}}", "--", id);
                Assert.Equal(0, actual.ExitCode); Assert.Equal(second.ResolvedImages["app:image-id"], actual.StandardOutput.Trim());
                var release = await Docker("exec", id, "/usr/bin/cat", "/release.txt");
                Assert.Equal(0, release.ExitCode); Assert.EndsWith(":second", release.StandardOutput);
            }
            var absent = await Docker("ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=" + project, "--filter", "label=com.docker.compose.service=zero");
            Assert.Equal(0, absent.ExitCode); Assert.Empty(absent.StandardOutput.Trim());
            Assert.NotEqual(0, (await Docker("image", "inspect", "--", project + "-inactive")).ExitCode);
        }
        finally
        {
            // Only this fixture's project, generated tags and project-labelled image IDs. Never prune or remove workload volumes.
            var cleanup = Path.Combine(root.Path, "build-only-cleanup.yaml");
            await File.WriteAllTextAsync(cleanup, $"services:\n  app:\n    image: {project}-app\n  zero:\n    image: {project}-zero\n");
            await Docker("compose", "--project-name", project, "--file", cleanup, "down", "--remove-orphans");
            await Docker("image", "rm", "--", project + "-app", project + "-zero");
            foreach (var id in imageIds)
            {
                var owner = await Docker("image", "inspect", "--format", "{{index .Config.Labels \"com.docker.compose.project\"}}", "--", id);
                if (owner.ExitCode == 0 && owner.StandardOutput.Trim() == project) await Docker("image", "rm", "--", id);
            }
        }
    }

    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task RealComposeBuildDeployHealthExecLifecycleAndCleanupUseOnlyDedicatedProject()
    {
        using var root = new AgentTestDirectory();
        var runner = new ProcessRunner(TimeProvider.System);
        var deploymentId = Guid.NewGuid();
        var project = "stanza-" + deploymentId.ToString("N");
        var image = "stanza-transport-test-" + deploymentId.ToString("N");
        var volume = project + "-runtime-test-data";
        var sentinel = "stanza-runtime-sentinel-" + Guid.NewGuid().ToString("N");
        var sentinelCreated = false;
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
                    volumes:
                      - .:/managed:ro
                      - state:/state
                    healthcheck:
                      test: ["CMD", "test", "-e", "/etc/os-release"]
                      interval: 1s
                      timeout: 1s
                      retries: 10
                volumes:
                  state:
                    name: {{volume}}
                """)),
            new("Dockerfile", Encoding.UTF8.GetBytes("FROM ubuntu:24.04\nRUN mkdir /state && chown 12345:12345 /state\nCOPY . /snapshot\nUSER 12345:12345\nCMD [\"/bin/sh\",\"-c\",\"echo stanza-transport-ready; exec sleep 300\"]\n")),
            // An implicit workspace .env must never override the explicitly resolved scopes.
            new(".env", Encoding.UTF8.GetBytes("STANZA_LAYER=implicit-unsafe\n")),
            new("nested/config/app.conf", Encoding.UTF8.GetBytes("non-root-readable-config\n")),
            new("empty/parent/leaf", [], IsDirectory: true)
        };
        AgentWorkRequest Request(string operation, AgentExecution? command = null) => new(Guid.NewGuid(), deploymentId, operation,
            operation is "start" or "stop" or "restart" or "remove" or "status" or "logs" ? [] : files,
            operation is "start" or "stop" or "restart" or "remove" or "status" or "logs" ? [] :
            [new("Global", "STANZA_LAYER=global\nGLOBAL_ONLY=unique-global-secret\n"), new("Agent", "STANZA_LAYER=agent\nAGENT_ONLY=unique-agent-secret\n"), new("Deployment", "STANZA_LAYER=deployment\n")],
            command, Policy: new(TimeoutSeconds: 120));
        var requests = new List<AgentWorkRequest>();
        async Task<AgentWorkResponse> Run(string operation, AgentExecution? command = null)
        {
            var request = Request(operation, command); requests.Add(request);
            var result = await executor.ExecuteAsync(request);
            Assert.True(result.Succeeded, operation + ": " + result.Code);
            if (sentinelCreated)
            {
                var unchanged = await Docker(["inspect", "--format", "{{.State.Running}}", sentinel]);
                Assert.Equal(0, unchanged.ExitCode); Assert.Equal("true", unchanged.StandardOutput.Trim());
            }
            return result;
        }
        Task<ProcessResult> Docker(IReadOnlyList<string> arguments) => runner.RunAsync(new("/usr/bin/docker", root.Path, arguments,
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(30)));
        try
        {
            var sentinelRun = await Docker(["run", "--detach", "--name", sentinel, "--label", "com.docker.compose.project=" + sentinel,
                "ubuntu:24.04", "/usr/bin/sleep", "300"]);
            Assert.Equal(0, sentinelRun.ExitCode); sentinelCreated = true;
            await Run("validate");
            await Run("build");
            await Run("deploy");
            var status = await Run("status");
            Assert.Contains("healthy", status.Output);
            using var observed = JsonDocument.Parse(status.Output!);
            Assert.Equal("running", Assert.Single(observed.RootElement.EnumerateArray()).GetProperty("State").GetString());
            var echo = await Run("container-exec", new("/usr/bin/printf", ["%s", "literal;$(not-shell)"], Service: "app"));
            Assert.Equal("literal;$(not-shell)", echo.Output);
            var environment = await Run("container-exec", new("/usr/bin/printenv", ["STANZA_LAYER"], Service: "app"));
            Assert.Equal("deployment", environment.Output!.Trim());
            Assert.Equal("12345", (await Run("container-exec", new("/usr/bin/id", ["-u"], Service: "app"))).Output!.Trim());
            Assert.Equal("non-root-readable-config", (await Run("container-exec", new("/usr/bin/cat", ["/managed/nested/config/app.conf"], Service: "app"))).Output!.Trim());
            await Run("container-exec", new("/usr/bin/test", ["-d", "/managed/empty/parent/leaf"], Service: "app"));
            var copiedFiles = await Run("container-exec", new("/usr/bin/find", ["/snapshot", "-type", "f"], Service: "app"));
            Assert.DoesNotContain("environment-0.env", copiedFiles.Output); Assert.DoesNotContain("environment-1.env", copiedFiles.Output);
            Assert.DoesNotContain("config.json", copiedFiles.Output); Assert.DoesNotContain("askpass", copiedFiles.Output);
            Assert.Equal("STANZA_LAYER=implicit-unsafe", (await Run("container-exec", new("/usr/bin/cat", ["/snapshot/.env"], Service: "app"))).Output!.Trim());
            Assert.Contains("stanza-transport-ready", (await Run("logs")).Output);
            await Run("container-exec", new("/bin/sh", ["-c", "printf '%s' persistent-test-data > /state/sentinel"], Service: "app"));
            await Run("restart"); await Run("stop"); await Run("start");
            Assert.Equal("persistent-test-data", (await Run("container-exec", new("/usr/bin/cat", ["/state/sentinel"], Service: "app"))).Output);
            var deployment = requests.Single(x => x.Operation == "deploy");
            Assert.True((await executor.ExecuteAsync(deployment)).Succeeded); // Durable completed replay, not a second deploy.
            await Run("remove");
            Assert.Equal("[]", (await Run("status")).Output);
            Assert.Equal(0, (await Docker(["volume", "inspect", "--", volume])).ExitCode);
        }
        finally
        {
            // No global prune, no volume deletion, and no access to any existing user's Compose project.
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["compose", "--project-name", project,
                "--file", WriteCleanupCompose(root.Path, image), "down", "--remove-orphans"],
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(30)));
            await runner.RunAsync(new("/usr/bin/docker", root.Path, ["image", "rm", "--", image],
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(30)));
            if (sentinelCreated) await Docker(["rm", "--force", "--", sentinel]);
            var ownedVolume = await Docker(["volume", "inspect", "--format", "{{index .Labels \"com.docker.compose.project\"}}", volume]);
            if (ownedVolume.ExitCode == 0 && ownedVolume.StandardOutput.Trim() == project)
                await Docker(["volume", "rm", "--", volume]);
        }
    }

    private static string WriteCleanupCompose(string directory, string image)
    {
        var path = Path.Combine(directory, "cleanup.yaml");
        File.WriteAllText(path, "services:\n  app:\n    image: " + image + "\n");
        return path;
    }
}
