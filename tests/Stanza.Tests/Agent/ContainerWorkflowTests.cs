using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class ContainerWorkflowTests
{
    [Theory]
    [InlineData("once", 2)][InlineData("all", 4)]
    public async Task ScriptsRunSequentiallyInVerifiedRunningReplicasWithLiteralVariables(string replicas, int expected)
    {
        using var root = new AgentTestDirectory();
        const string value = "$(touch /forbidden); 'quoted'\nline";
        var request = AgentWorkExecutorTests.Request("container-script") with { Files = [], EnvironmentFiles = [],
            Execution = new("/bin/bash", [], Mode: "script", Script: "printf '%s' \"$MESSAGE\"", Services: ["app", "worker"], Replicas: replicas,
                Environment: new Dictionary<string, string> { ["MESSAGE"] = value }) };
        var ids = new[] { new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64) };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => new(0, p.Arguments[0] switch
        {
            "ps" => p.Arguments.Contains("label=com.docker.compose.service=app") ? string.Join('\n', ids.Take(2)) : string.Join('\n', ids.Skip(2)),
            "inspect" => JsonSerializer.Serialize(new { Id = p.Arguments.Last(), Config = new { Labels = new Dictionary<string, string> {
                ["com.docker.compose.project"] = "stanza-" + request.DeploymentId.ToString("N"),
                ["com.docker.compose.service"] = ids.Take(2).Contains(p.Arguments.Last()) ? "app" : "worker", ["com.docker.compose.oneoff"] = "False" } }, State = new { Running = true } }),
            _ => "ok"
        }, "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        var commands = runner.Requests.Where(p => p.Arguments[0] == "exec").ToArray();
        Assert.Equal(expected, commands.Length);
        Assert.All(commands, p => { Assert.Contains("MESSAGE=" + value, p.Arguments); Assert.Equal(request.Execution.Script, p.StandardInput); Assert.DoesNotContain("--tty", p.Arguments); });
        Assert.Equal(4, runner.Requests.TakeWhile(p => p.Arguments[0] != "exec").Count(p => p.Arguments[0] == "inspect"));
        Assert.All(runner.Requests.Where(p => p.Arguments[0] == "ps"), p => Assert.Contains("label=com.docker.compose.oneoff=False", p.Arguments));
        var count = runner.Requests.Count;
        Assert.True((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        Assert.Equal(count, runner.Requests.Count);
    }

    [Theory]
    [InlineData(false, true, false)][InlineData(true, false, false)][InlineData(true, true, true)]
    public async Task MissingStoppedOrForeignTargetsFailBeforeAnyScript(bool exists, bool running, bool foreign)
    {
        using var root = new AgentTestDirectory();
        var request = AgentWorkExecutorTests.Request("container-script") with { Files = [], EnvironmentFiles = [],
            Execution = new("/bin/bash", [], Mode: "script", Script: "echo ready", Services: ["app", "worker"], Replicas: "all") };
        var id = new string('a', 64);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => new(0, p.Arguments[0] == "ps" ? exists ? id : "" :
            JsonSerializer.Serialize(new { Id = id, Config = new { Labels = new Dictionary<string, string> {
                ["com.docker.compose.project"] = foreign ? "other" : "stanza-" + request.DeploymentId.ToString("N"), ["com.docker.compose.service"] = "app" } }, State = new { Running = running } }), "", false, false) };
        Assert.False((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        Assert.DoesNotContain(runner.Requests, p => p.Arguments[0] == "exec");
    }

    [Fact]
    public async Task UnsafeVariableEnvironmentIsRejectedBeforeDockerRuns()
    {
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner();
        var request = AgentWorkExecutorTests.Request("container-script") with { Execution = new("/bin/bash", [], Mode: "script", Script: "echo ready",
            Services: ["app"], Replicas: "once", Environment: new Dictionary<string, string> { ["BASH_ENV"] = "/startup" }) };
        Assert.Equal("invalid_script", (await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Code);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task FailureStopsRemainingReplicasAndReplayNeverRepeatsTheFailedScript()
    {
        using var root = new AgentTestDirectory();
        var request = AgentWorkExecutorTests.Request("container-script") with { Files = [], EnvironmentFiles = [],
            Execution = new("/bin/bash", [], Mode: "script", Script: "false", Services: ["app"], Replicas: "all") };
        var ids = new[] { new string('a', 64), new string('b', 64) };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => p.Arguments[0] == "exec" ? new(1, "", "failed", false, false) :
            new(0, p.Arguments[0] == "ps" ? string.Join('\n', ids) : JsonSerializer.Serialize(new { Id = p.Arguments.Last(), Config = new { Labels = new Dictionary<string, string> {
                ["com.docker.compose.project"] = "stanza-" + request.DeploymentId.ToString("N"), ["com.docker.compose.service"] = "app" } }, State = new { Running = true } }), "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.False(result.Succeeded); Assert.Equal("script_failed", result.Code); Assert.True(result.ApplyStarted);
        Assert.Single(runner.Requests, p => p.Arguments[0] == "exec");
        var count = runner.Requests.Count;
        Assert.Equal("script_failed", (await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Code);
        Assert.Equal(count, runner.Requests.Count);
    }

    [Theory]
    [InlineData("worker", "False")][InlineData("app", "True")]
    public async Task MismatchedServiceAndOneOffContainersAreRejectedEvenIfListedByDocker(string service, string oneoff)
    {
        using var root = new AgentTestDirectory(); var id = new string('a', 64);
        var request = AgentWorkExecutorTests.Request("container-script") with { Files = [], EnvironmentFiles = [],
            Execution = new("/bin/bash", [], Mode: "script", Script: "echo ready", Services: ["app"], Replicas: "all") };
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = p => new(0, p.Arguments[0] == "ps" ? id :
            JsonSerializer.Serialize(new { Id = id, Config = new { Labels = new Dictionary<string, string> {
                ["com.docker.compose.project"] = "stanza-" + request.DeploymentId.ToString("N"), ["com.docker.compose.service"] = service,
                ["com.docker.compose.oneoff"] = oneoff } }, State = new { Running = true } }), "", false, false) };
        Assert.Equal("container_scope_denied", (await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Code);
        Assert.DoesNotContain(runner.Requests, p => p.Arguments[0] == "exec");
    }
}
