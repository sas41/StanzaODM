using System.Text.Json;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentActionTests
{
    [Theory]
    [InlineData(true, true, true)][InlineData(false, true, false)][InlineData(true, false, false)]
    public async Task ResetDatabaseResolvesRunningServiceByDeploymentLabelsWithoutDraftCompose(bool matchingService, bool running, bool success)
    {
        using var directory = new AgentTestDirectory();
        var id = new string('c', 64);
        var request = new AgentWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "container-exec", [], [],
            Execution: new("/usr/bin/reset-db", ["--testing"], Service: "app"));
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => new(0,
            p.Arguments[0] == "ps" ? id + "\n" : p.Arguments[0] == "inspect" ? JsonSerializer.Serialize(new
            {
                Id = id, Config = new { Labels = new Dictionary<string, string> {
                    ["com.docker.compose.project"] = "stanza-" + request.DeploymentId.ToString("N"),
                    ["com.docker.compose.service"] = matchingService ? "app" : "other" } }, State = new { Running = running }
            }) : "reset-complete", "", false, false) };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(success, response.Succeeded);
        Assert.Contains(runner.Requests[0].Arguments, a => a == "label=com.docker.compose.service=app");
        Assert.DoesNotContain(runner.Requests, p => p.Arguments.Contains("compose"));
        if (success) { var exec = Assert.Single(runner.Requests, p => p.Arguments[0] == "exec"); Assert.Contains(id, exec.Arguments); Assert.Equal("--testing", exec.Arguments[^1]); }
        else Assert.DoesNotContain(runner.Requests, p => p.Arguments[0] == "exec");
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ImageSelectionUsesPrivateOverrideWithoutRewritingComposeAndRejectsBuildServices(bool built)
    {
        using var directory = new AgentTestDirectory();
        var request = AgentWorkExecutorTests.Request() with { ImageOverrides = new Dictionary<string, string> { ["app"] = "ghcr.io/org/app@sha256:" + new string('b', 64) } };
        string? overrideText = null;
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p =>
        {
            var path = p.Arguments.FirstOrDefault(a => a.EndsWith("action-images.json", StringComparison.Ordinal));
            if (path is not null) overrideText = File.ReadAllText(path);
            return new(0, built ? "{\"services\":{\"app\":{\"build\":{\"context\":\".\"}}}}" : "{\"services\":{\"app\":{\"image\":\"ghcr.io/org/app\"}}}", "", false, false);
        } };
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(!built, response.Succeeded);
        if (built) Assert.Equal("image_override_service_unavailable", response.Code);
        else
        {
            using var document = JsonDocument.Parse(overrideText!);
            Assert.Equal(request.ImageOverrides["app"], document.RootElement.GetProperty("services").GetProperty("app").GetProperty("image").GetString());
            Assert.Equal(request.Files[0].Content, File.ReadAllBytes(Path.Combine(runner.Requests[0].WorkingDirectory, "compose.yaml")));
            Assert.All(runner.Requests, p => Assert.False(Directory.Exists(p.Environment["DOCKER_CONFIG"])));
        }
    }

    [Fact]
    public async Task TagResolutionPinsRegistryIndexDigestBeforeExecutionWithoutComposeOrPull()
    {
        using var directory = new AgentTestDirectory(); var digest = "sha256:" + new string('d', 64);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0, JsonSerializer.Serialize(new { digest }), "", false, false) };
        var request = new AgentWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "resolve-action-inputs", [], [],
            ImageOverrides: new Dictionary<string, string> { ["app"] = "ghcr.io/org/app:v1" });
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code); Assert.Equal(digest, response.ResolvedImages!["app"]);
        var process = Assert.Single(runner.Requests); Assert.Equal("buildx", process.Arguments[0]); Assert.DoesNotContain("pull", process.Arguments);
    }

    [Theory]
    [InlineData("refs/heads/feature/qa", true)][InlineData("refs/heads/wrong", false)]
    public async Task BranchResolutionReadsExactRemoteRefWithoutCloningAndRejectsMismatchedRefs(string returnedRef, bool success)
    {
        using var directory = new AgentTestDirectory(); var commit = new string('e', 40);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0, commit + "\t" + returnedRef + "\n", "", false, false) };
        var request = new AgentWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "resolve-action-inputs", [], [],
            Sources: [new("https://git.example.test/org/app.git", "refs/heads/feature/qa", "src")]);
        var response = await AgentWorkExecutorTests.Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(success, response.Succeeded);
        if (success) Assert.Equal(commit, response.ResolvedSources!["src"]);
        var process = Assert.Single(runner.Requests); Assert.Contains("ls-remote", process.Arguments); Assert.DoesNotContain("fetch", process.Arguments);
    }
}
