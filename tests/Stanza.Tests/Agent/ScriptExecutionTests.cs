using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class ScriptExecutionTests
{
    [Fact]
    public async Task ActualBashReceivesVariablesAsDataWithoutEvaluatingTheirContents()
    {
        using var root = new AgentTestDirectory();
        const string value = "$(printf injected); 'quotes'\nsecond line";
        var executor = new AgentWorkExecutor(new ProcessRunner(TimeProvider.System), Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        var request = Request(script: "printf '%s' \"$MESSAGE\"");
        var result = await executor.ExecuteAsync(request with { Execution = request.Execution! with {
            Environment = new Dictionary<string, string> { ["MESSAGE"] = value } } });
        Assert.True(result.Succeeded, result.Code); Assert.Equal(value, result.Output);
    }

    [Theory]
    [InlineData("docker system prune --force")]
    [InlineData("docker image prune --all --force")]
    [InlineData("docker volume ls")]
    public async Task AgentLevelDockerAdministrationDoesNotRequireADeploymentOrPrivilegedHostShell(string script)
    {
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0, "ok", "", false, false) };
        var request = Request(script: script) with { DeploymentId = Guid.Empty, Files = [], EnvironmentFiles = [] };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        var process = Assert.Single(runner.Requests);
        Assert.Equal("/bin/bash", process.Executable); Assert.Equal(script, process.StandardInput);
        Assert.Equal("unix:///var/run/docker.sock", process.Environment["DOCKER_HOST"]);
        Assert.DoesNotContain("STANZA_PROJECT_NAME", process.Environment.Keys);
        Assert.DoesNotContain("nsenter", process.Executable);
    }

    private static AgentWorkRequest Request(string kind = "docker-script", string script = "docker ps\n") =>
        AgentWorkExecutorTests.Request(kind) with
        {
            Execution = new("/bin/bash", [], Service: kind == "container-script" ? "app" : null, Mode: "script", Script: script)
        };

    [Fact]
    public async Task DockerScriptUsesAgentBashAndStdinWithoutHostNamespacesOrShellInterpolation()
    {
        using var root = new AgentTestDirectory();
        const string script = "docker ps --filter \"label=com.docker.compose.project=${STANZA_PROJECT_NAME}\"\n";
        var request = Request(script: script); var runner = new AgentWorkExecutorTests.FakeRunner { Result = _ => new(0, "result", "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        var command = Assert.Single(runner.Requests);
        Assert.Equal("/bin/bash", command.Executable); Assert.Equal(script, command.StandardInput);
        Assert.Equal(["--noprofile", "--norc", "-e", "-o", "pipefail", "-s", "--"], command.Arguments);
        Assert.Equal("stanza-" + request.DeploymentId.ToString("N"), command.Environment["STANZA_PROJECT_NAME"]);
        Assert.Equal("unix:///var/run/docker.sock", command.Environment["DOCKER_HOST"]);
        Assert.DoesNotContain("nsenter", command.Executable); Assert.DoesNotContain(script, command.ToString());
        Assert.True(result.ApplyStarted);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task ContainerScriptPinsVerifiedContainerAndNeedsNoComposeOrGit(bool matching, bool running, bool succeeds)
    {
        using var root = new AgentTestDirectory();
        var request = Request("container-script") with { Files = [], EnvironmentFiles = [], Sources = [new("ssh://unavailable/repo", "main", "source")] };
        var id = new string('a', 64);
        var runner = new AgentWorkExecutorTests.FakeRunner { Result = invocation => new(0, invocation.Arguments[0] switch
        {
            "ps" => id,
            "inspect" => JsonSerializer.Serialize(new { Id = id, Config = new { Labels = new Dictionary<string, string>
                { ["com.docker.compose.project"] = matching ? "stanza-" + request.DeploymentId.ToString("N") : "unrelated", ["com.docker.compose.service"] = "app" } }, State = new { Running = running } }),
            _ => "script output"
        }, "", false, false) };
        var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.All(runner.Requests, invocation => Assert.EndsWith("docker", invocation.Executable));
        if (succeeds)
        {
            var exec = Assert.Single(runner.Requests, r => r.Arguments[0] == "exec");
            Assert.Contains("--interactive", exec.Arguments); Assert.DoesNotContain("--tty", exec.Arguments);
            Assert.Contains(id, exec.Arguments); Assert.Contains("/bin/bash", exec.Arguments);
            Assert.Contains("BASH_ENV=/dev/null", exec.Arguments); Assert.Equal(request.Execution!.Script, exec.StandardInput);
        }
        else Assert.DoesNotContain(runner.Requests, r => r.Arguments[0] == "exec");
    }

    [Fact]
    public async Task InvalidScriptsFailBeforeAnyProcessOrCheckout()
    {
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner();
        var request = Request() with { Sources = [new("https://example.test/repo", "main", "source")] };
        var command = request.Execution!;
        foreach (var invalid in new[] { command with { Script = null }, command with { Script = new('a', 65537) },
            command with { Script = "nul\0" }, command with { Mode = "exec" }, command with { Executable = "/bin/sh" },
            command with { Arguments = ["-c", "bad"] }, command with { Container = "other" },
            command with { User = "root" }, command with { WorkingDirectory = "/etc" } })
        {
            var result = await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request with { Id = Guid.NewGuid(), Execution = invalid });
            Assert.False(result.Succeeded); Assert.False(result.ApplyStarted);
        }
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("printf '%s' 'literal;$(not-a-command)'", true, "literal;$(not-a-command)")]
    [InlineData("false\nprintf 'must-not-run'", false, "")]
    [InlineData("false | true\nprintf 'must-not-run'", false, "")]
    public async Task ActualBashPreservesAuthoredScriptAndPropagatesCommandAndPipelineFailures(string script, bool succeeded, string output)
    {
        using var root = new AgentTestDirectory();
        var executor = new AgentWorkExecutor(new ProcessRunner(TimeProvider.System), Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        var result = await executor.ExecuteAsync(Request(script: script));
        Assert.Equal(succeeded, result.Succeeded);
        if (succeeded) Assert.Equal(output, result.Output);
        else Assert.Equal("script_failed", result.Code);
    }

    [Fact]
    public async Task ScriptReplayDoesNotRunTwiceAndBodyChangesConflict()
    {
        using var root = new AgentTestDirectory(); var runner = new AgentWorkExecutorTests.FakeRunner(); var request = Request();
        Assert.True((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        Assert.True((await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Succeeded);
        Assert.Equal("request_id_conflict", (await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request with
            { Execution = request.Execution! with { Script = "docker version" } })).Code);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task CancellingActualBashStopsExecutionAndNeverPermitsBlindReplay()
    {
        using var root = new AgentTestDirectory(); using var cancellation = new CancellationTokenSource();
        var executor = new AgentWorkExecutor(new ProcessRunner(TimeProvider.System), Options.Create(new AgentOptions { PersistentDataRoot = root.Path }),
            Options.Create(new DockerRuntimeOptions()), Options.Create(new HostExecutionOptions()));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Output += (_, chunk) =>
        {
            if (chunk.Text.Contains("script-started", StringComparison.Ordinal)) started.TrySetResult();
            return Task.CompletedTask;
        };
        var request = Request(script: "printf 'script-started\\n'\nexec sleep 30\n");
        var work = executor.ExecuteAsync(request, cancellation.Token);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Succeeded); Assert.True(result.ExecutionStopped); Assert.True(result.ApplyStarted);
        Assert.Equal("uncertain", result.Code);
        var runner = new AgentWorkExecutorTests.FakeRunner();
        Assert.Equal("uncertain", (await AgentWorkExecutorTests.Executor(root.Path, runner).ExecuteAsync(request)).Code);
        Assert.Empty(runner.Requests);
    }
}
