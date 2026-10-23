using System.Text.Json;
using Stanza.Agent.Runtime;

namespace Stanza.Tests.Agent;

public sealed class ComposeConfigurationFactAttribute : FactAttribute
{
    public ComposeConfigurationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STANZA_TEST_COMPOSE") != "1" && Environment.GetEnvironmentVariable("STANZA_TEST_DOCKER") != "1")
            Skip = "Set STANZA_TEST_COMPOSE=1 to verify generated names with the installed Compose CLI (no Docker daemon required).";
    }
}

public sealed class ComposeImageResolutionTests
{
    [ComposeConfigurationFact]
    public async Task ComposeReportsGeneratedNamesPerServiceHonorsTheProjectFlagAndExcludesInactiveProfilesWithoutADaemon()
    {
        using var directory = new AgentTestDirectory();
        var file = Path.Combine(directory.Path, "compose.yaml");
        await File.WriteAllTextAsync(file, """
            name: authored-name-is-not-the-runtime-project
            services:
              build:
                build:
                  context: .
                  tags: [additional-tag:latest]
              explicit:
                image: local/explicit:latest
                build: .
              inactive:
                profiles: [manual]
                build: .
              zero:
                build: .
                deploy:
                  replicas: 0
            """);
        var project = "stanza-" + Guid.NewGuid().ToString("N");
        string[] prefix = ["compose", "--project-directory", directory.Path, "--project-name", project, "--file", file];
        var runner = new ProcessRunner(TimeProvider.System);
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = directory.Path,
            ["DOCKER_HOST"] = "unix://" + Path.Combine(directory.Path, "no-daemon.sock") };
        async Task<string> Config(params string[] args)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", directory.Path, [.. prefix, "config", .. args], environment, TimeSpan.FromSeconds(15)));
            Assert.True(result.ExitCode == 0 && !result.TimedOut, result.StandardError);
            return result.StandardOutput;
        }
        using var normalized = JsonDocument.Parse(await Config("--format", "json"));
        var services = normalized.RootElement.GetProperty("services");
        Assert.False(services.TryGetProperty("inactive", out _));
        Assert.False(services.GetProperty("build").TryGetProperty("image", out _));
        Assert.Equal(project + "-build", (await Config("--images", "--", "build")).Trim());
        Assert.Equal("local/explicit:latest", (await Config("--images", "--", "explicit")).Trim());
        Assert.Equal(project + "-zero", (await Config("--images", "--", "zero")).Trim());
    }
}
