using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Agent;

public sealed class AgentWorkExecutorTests
{
    internal static AgentWorkRequest Request(string operation = "validate") => new(Guid.NewGuid(), Guid.NewGuid(), operation,
        [new("compose.yaml", Encoding.UTF8.GetBytes("services:\n  app:\n    image: busybox:latest\n"))],
        [new("Global", "VALUE=global\n"), new("Agent", "VALUE=agent\n"), new("Deployment", "VALUE=deployment\n")]);

    internal static AgentWorkExecutor Executor(string root, IProcessRunner runner, bool host = false) => new(runner,
        Options.Create(new AgentOptions { PersistentDataRoot = root }), Options.Create(new DockerRuntimeOptions()),
        Options.Create(new HostExecutionOptions { Enabled = host }));

    internal sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Func<ProcessRequest, ProcessResult> Result { get; set; } = request => new(0,
            request.Arguments.Contains("config") ? "{\"services\":{\"app\":{\"image\":\"busybox:latest\"}}}" : "", "", false, false);
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result(request));
        }
    }

    [Fact]
    public async Task MaterializesExactSnapshotUsesThreeExplicitEnvironmentFilesAndStableProject()
    {
        using var directory = new AgentTestDirectory();
        var runner = new FakeRunner(); var request = Request();
        var result = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        var process = Assert.Single(runner.Requests);
        Assert.Equal("stanza-" + request.DeploymentId.ToString("N"), process.Arguments[process.Arguments.IndexOf("--project-name") + 1]);
        Assert.Equal(3, process.Arguments.Count(x => x == "--env-file"));
        Assert.Equal("true", process.Environment["COMPOSE_DISABLE_ENV_FILE"]);
        Assert.DoesNotContain("DOTNET_ROOT", process.Environment.Keys);
        Assert.Equal(request.Files[0].Content, File.ReadAllBytes(Path.Combine(process.WorkingDirectory, "compose.yaml")));
        Assert.Equal("VALUE=deployment\n", File.ReadAllText(Path.Combine(process.WorkingDirectory, ".stanza/environment-2.env")));
        Assert.False(Directory.Exists(process.Environment["DOCKER_CONFIG"]));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData(".git/config")]
    [InlineData(".STANZA/evil")]
    public async Task RejectsTraversalAndReservedPathsBeforeProcesses(string path)
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner();
        var response = await Executor(directory.Path, runner).ExecuteAsync(Request() with { Files = [new(path, [1])] });
        Assert.False(response.Succeeded); Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("A/first", "a/second")]
    [InlineData("same", "SAME")]
    [InlineData("a", "a/b")]
    public async Task RejectsCaseAndDirectoryCollisions(string first, string second)
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner();
        var response = await Executor(directory.Path, runner).ExecuteAsync(Request() with { Files = [new(first, [1]), new(second, [2])] });
        Assert.False(response.Succeeded); Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task RejectsSymlinkStorageWithoutTouchingDestination()
    {
        using var directory = new AgentTestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(directory.Path, "linked"), Path.Combine(directory.Path, "outside"));
        var runner = new FakeRunner();
        var result = await Executor(Path.Combine(directory.Path, "linked"), runner).ExecuteAsync(Request());
        Assert.False(result.Succeeded); Assert.Empty(runner.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory.Path, "outside")));
    }

    [Fact]
    public async Task CompletedLedgerReplaysAfterExecutorRestartAndRejectsChangedSnapshot()
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner(); var request = Request();
        var first = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(first.Succeeded);
        var replay = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(first.Code, replay.Code); Assert.Single(runner.Requests);
        var conflict = await Executor(directory.Path, runner).ExecuteAsync(request with { Operation = "remove" });
        Assert.Equal("request_id_conflict", conflict.Code); Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task StartedButInterruptedLedgerNeverRepeatsExecution()
    {
        using var directory = new AgentTestDirectory(); var runner = new CancelledRunner(); var request = Request();
        Assert.Equal("uncertain", (await Executor(directory.Path, runner).ExecuteAsync(request)).Code);
        var fake = new FakeRunner();
        Assert.Equal("uncertain", (await Executor(directory.Path, fake).ExecuteAsync(request)).Code);
        Assert.Empty(fake.Requests);
    }
    private sealed class CancelledRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    [Fact]
    public async Task DisabledAndInvalidPolicyNeverInvokeRuntime()
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner(); var executor = Executor(directory.Path, runner);
        Assert.Equal("execution_disabled", (await executor.ExecuteAsync(Request() with { Policy = new(false) })).Code);
        Assert.Equal("invalid_runtime_policy", (await executor.ExecuteAsync(Request() with { Policy = new(TimeoutSeconds: 0) })).Code);
        Assert.Equal("invalid_runtime_policy", (await executor.ExecuteAsync(Request() with { Policy = new(DockerEndpoint: "tcp://unsafe:2375") })).Code);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("ssh://git@example.com/a.git")]
    [InlineData("git@example.com:a.git")]
    [InlineData("file:///etc/secret")]
    [InlineData("ext::sh -c evil")]
    [InlineData("https://user:secret@example.com/a.git")]
    [InlineData("http://example.com/a.git")]
    public async Task RejectsUnsupportedGitProtocolsAndUrlCredentials(string url)
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner();
        var result = await Executor(directory.Path, runner).ExecuteAsync(Request() with { Sources = [new(url, "main", "source")] });
        Assert.Equal("unsupported_git_source", result.Code); Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task GitBranchesAreStructuredBoundedAndCredentialsNeverInArgumentsOrDiagnostics()
    {
        using var directory = new AgentTestDirectory();
        var runner = new FakeRunner { Result = _ => new(0, new string('a', 40) + "\trefs/heads/feature/quoted;safe\n" + new string('b', 40) + "\trefs/heads/main\n", "", false, false) };
        var request = Request("git-branches") with { Sources = [new("https://git.example/repo", "main", "source", "alice", "sensitive-password")] };
        var result = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(["feature/quoted;safe", "main"], JsonSerializer.Deserialize<string[]>(result.Output!)!);
        var process = Assert.Single(runner.Requests);
        Assert.DoesNotContain("sensitive-password", string.Join(' ', process.Arguments));
        Assert.Equal("sensitive-password", process.Environment["STANZA_GIT_PASSWORD"]);
        Assert.Contains("http.followRedirects=false", process.Arguments);
        Assert.DoesNotContain("sensitive-password", process.ToString());
        Assert.False(File.Exists(process.Environment["GIT_ASKPASS"]));
    }

    [Fact]
    public async Task GitChecksOutPinnedCommitAndReturnsResolvedIdentity()
    {
        using var directory = new AgentTestDirectory(); var commit = new string('a', 40);
        var runner = new FakeRunner { Result = request => new(0,
            request.Arguments.Contains("rev-parse") ? commit + "\n" : request.Arguments.Contains("config") ? "{\"services\":{}}" : "", "", false, false) };
        var result = await Executor(directory.Path, runner).ExecuteAsync(Request() with { Sources = [new("https://git.example/repo", "main", "source")] });
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(commit, result.ResolvedSources!["source"]);
        var checkout = Assert.Single(runner.Requests, x => x.Arguments.Contains("checkout"));
        Assert.Equal(commit, checkout.Arguments[^1]); Assert.Contains("submodule.recurse=false", checkout.Arguments);
    }

    [Fact]
    public async Task SubmodulesAndCheckoutSymlinksAreRejected()
    {
        foreach (var symlink in new[] { false, true })
        {
            using var directory = new AgentTestDirectory();
            var runner = new FakeRunner { Result = request =>
            {
                if (request.Arguments.Contains("checkout"))
                {
                    if (symlink) File.CreateSymbolicLink(Path.Combine(request.WorkingDirectory, "escape"), "/etc/passwd");
                    else File.WriteAllText(Path.Combine(request.WorkingDirectory, ".gitmodules"), "[submodule]");
                }
                return new(0, request.Arguments.Contains("rev-parse") ? new string('a', 40) : "", "", false, false);
            }};
            var result = await Executor(directory.Path, runner).ExecuteAsync(Request() with { Sources = [new("https://git.example/repo", "main", "source")] });
            Assert.False(result.Succeeded); Assert.DoesNotContain(runner.Requests, x => x.Executable.EndsWith("docker", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RegistryCheckUsesPasswordStdinAndTemporaryPrivateDockerConfiguration()
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner();
        var request = Request("registry-check") with { DeploymentId = Guid.Empty, Registries = [new("registry.example", "alice", "private-password")] };
        var response = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.True(response.Succeeded, response.Code);
        var process = Assert.Single(runner.Requests);
        Assert.Equal("private-password\n", process.StandardInput);
        Assert.DoesNotContain("private-password", string.Join(' ', process.Arguments));
        Assert.DoesNotContain("private-password", process.ToString());
        Assert.False(Directory.Exists(process.Environment["DOCKER_CONFIG"]));
    }

    [Fact]
    public async Task DeployRunsConfigBuildEligiblePullAndWaitInOrderWithoutRemovingVolumes()
    {
        using var directory = new AgentTestDirectory();
        var runner = new FakeRunner { Result = request => new(0, request.Arguments.Contains("config") ?
            "{\"services\":{\"built\":{\"build\":{\"context\":\".\"},\"image\":\"local\"},\"remote\":{\"image\":\"busybox\"}}}" : "[]", "", false, false) };
        Assert.True((await Executor(directory.Path, runner).ExecuteAsync(Request("deploy"))).Succeeded);
        Assert.Equal(5, runner.Requests.Count);
        Assert.Contains("config", runner.Requests[0].Arguments); Assert.Contains("build", runner.Requests[1].Arguments);
        Assert.Contains("pull", runner.Requests[2].Arguments); Assert.Equal("remote", runner.Requests[2].Arguments[^1]);
        Assert.DoesNotContain("built", runner.Requests[2].Arguments);
        Assert.Contains("--wait", runner.Requests[3].Arguments); Assert.Contains("--no-build", runner.Requests[3].Arguments);
        Assert.DoesNotContain(runner.Requests, x => x.Arguments.Contains("--volumes"));
    }

    [Fact]
    public async Task HealthOrRuntimeFailureDoesNotReportDeploymentSuccessOrLeakErrorText()
    {
        using var directory = new AgentTestDirectory();
        var runner = new FakeRunner { Result = request => request.Arguments.Contains("up")
            ? new(1, "secret-config", "secret-password", false, false)
            : new(0, "{\"services\":{}}", "", false, false) };
        var result = await Executor(directory.Path, runner).ExecuteAsync(Request("deploy"));
        Assert.False(result.Succeeded); Assert.Null(result.Output); Assert.DoesNotContain("secret", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerExecAlwaysChecksProjectAndRunningStateBeforeExec(bool matching)
    {
        using var directory = new AgentTestDirectory(); var request = Request("container-exec") with
            { Execution = new("/usr/bin/printf", ["%s", "$(evil); quoted"], Container: "arbitrary-container") };
        var runner = new FakeRunner { Result = invocation => new(0, invocation.Arguments.Contains("inspect")
            ? JsonSerializer.Serialize(new { Id = new string('a', 64), Config = new { Labels = new Dictionary<string, string>
                { ["com.docker.compose.project"] = matching ? "stanza-" + request.DeploymentId.ToString("N") : "unrelated" } }, State = new { Running = true } })
            : "safe-output", "", false, false) };
        var response = await Executor(directory.Path, runner).ExecuteAsync(request);
        Assert.Equal(matching, response.Succeeded);
        if (matching)
        {
            var exec = Assert.Single(runner.Requests, x => x.Arguments.Contains("exec"));
            Assert.Contains(new string('a', 64), exec.Arguments); Assert.DoesNotContain("arbitrary-container", exec.Arguments);
            Assert.Equal("$(evil); quoted", exec.Arguments[^1]); Assert.DoesNotContain("-it", exec.Arguments);
        }
        else { Assert.Equal("container_scope_denied", response.Code); Assert.Single(runner.Requests); }
    }

    [Fact]
    public async Task HostExecIsDisabledByDefaultAndNeverPretendsAgentContainerIsHost()
    {
        using var directory = new AgentTestDirectory(); var runner = new FakeRunner();
        var request = Request("host-exec") with { DeploymentId = Guid.Empty, Execution = new("/usr/bin/id", []) };
        Assert.Equal("host_execution_disabled", (await Executor(directory.Path, runner).ExecuteAsync(request)).Code);
        var result = await Executor(directory.Path, runner, true).ExecuteAsync(request with { Id = Guid.NewGuid() });
        if (new FileInfo("/proc/1/ns/mnt").LinkTarget == new FileInfo("/proc/self/ns/mnt").LinkTarget)
        { Assert.Equal("host_namespace_prerequisites", result.Code); Assert.Empty(runner.Requests); }
        else Assert.All(runner.Requests, invocation => Assert.Equal("/usr/bin/nsenter", invocation.Executable));
    }

    [Fact]
    public async Task StoppedContainerCannotBeExecutedAndRequestedArgumentsRemainTyped()
    {
        using var directory = new AgentTestDirectory(); var request = Request("container-exec") with
            { Execution = new("/bin/echo", [], Container: "stopped") };
        var runner = new FakeRunner { Result = _ => new(0, JsonSerializer.Serialize(new { Id = new string('b', 64),
            Config = new { Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "stanza-" + request.DeploymentId.ToString("N") } },
            State = new { Running = false } }), "", false, false) };
        Assert.Equal("container_not_running", (await Executor(directory.Path, runner).ExecuteAsync(request)).Code);
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../credentials")]
    [InlineData("https://git.example/untracked-build-context")]
    public async Task ComposeBuildContextCannotEscapeSnapshot(string buildContext)
    {
        using var directory = new AgentTestDirectory();
        var runner = new FakeRunner { Result = _ => new(0,
            JsonSerializer.Serialize(new { services = new { app = new { build = new { context = buildContext } } } }), "", false, false) };
        var response = await Executor(directory.Path, runner).ExecuteAsync(Request("build"));
        Assert.False(response.Succeeded); Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ImageDigestsSelectDeploymentPlatformExcludeBuildsAndReturnStructuredIdentities()
    {
        using var directory = new AgentTestDirectory(); var arm = "sha256:" + new string('a', 64); var amd = "sha256:" + new string('b', 64);
        var runner = new FakeRunner { Result = request => new(0,
            request.Arguments.Contains("config") ? "{\"services\":{\"app\":{\"image\":\"registry.example/app:tag\",\"platform\":\"linux/arm64/v8\"},\"built\":{\"image\":\"local\",\"build\":{\"context\":\".\"}}}}" :
            request.Arguments.Contains("version") ? "linux/amd64\n" :
            JsonSerializer.Serialize(new { digest = "sha256:" + new string('c', 64), manifests = new object[] {
                new { digest = arm, platform = new { os = "linux", architecture = "arm64", variant = "v8" } },
                new { digest = amd, platform = new { os = "linux", architecture = "amd64" } } } }), "", false, false) };
        var response = await Executor(directory.Path, runner).ExecuteAsync(Request("image-digests"));
        Assert.True(response.Succeeded, response.Code); Assert.Equal(arm, response.ResolvedImages!["app"]);
        Assert.DoesNotContain("built", response.ResolvedImages.Keys);
        Assert.Single(runner.Requests, invocation => invocation.Arguments.Contains("imagetools"));
    }

    [Fact]
    public async Task OutputAndTimeoutErrorsNeverExposePartialSensitiveOutput()
    {
        foreach (var timedOut in new[] { false, true })
        {
            using var directory = new AgentTestDirectory();
            var runner = new FakeRunner { Result = _ => new(1, "private-output", "private-error", !timedOut, timedOut) };
            var response = await Executor(directory.Path, runner).ExecuteAsync(Request());
            Assert.Equal(timedOut ? "uncertain" : "runtime_output_limit", response.Code);
            Assert.Null(response.Output); Assert.DoesNotContain("private", response.ToString());
        }
    }

    [Fact]
    public void HostEnvironmentAndArgumentsAreAppliedOnlyInsideTargetNamespaces()
    {
        var arguments = AgentWorkExecutor.BuildHostArguments(new("/usr/bin/printf", ["%s", "$(untrusted);not-a-shell"],
            WorkingDirectory: "/srv/app", Environment: new Dictionary<string, string> { ["LD_PRELOAD"] = "/host-only/library.so" }));
        Assert.Contains("--target", arguments); Assert.Contains("--mount", arguments); Assert.Contains("--pid", arguments);
        Assert.Contains("--root", arguments); Assert.Contains("--wd=/srv/app", arguments);
        Assert.True(arguments.IndexOf("LD_PRELOAD=/host-only/library.so") > arguments.IndexOf("/usr/bin/env"));
        Assert.Contains("--ignore-environment", arguments); Assert.Equal("$(untrusted);not-a-shell", arguments[^1]);
        Assert.DoesNotContain("/bin/sh", arguments);
    }

    [Fact]
    public async Task StalePrivateCredentialsAreRemovedBeforeAnyNewCommand()
    {
        using var directory = new AgentTestDirectory();
        var stale = Path.Combine(directory.Path, "credentials", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale); File.WriteAllText(Path.Combine(stale, "config.json"), "old-private-credential");
        var runner = new FakeRunner();
        Assert.True((await Executor(directory.Path, runner).ExecuteAsync(Request())).Succeeded);
        Assert.False(Directory.Exists(stale));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory.Path, "credentials")));
    }

    [Fact]
    public async Task ExclusiveProcessLeaseDoesNotAllowDeletingAnotherProcessesActiveCredentials()
    {
        using var directory = new AgentTestDirectory();
        var credentialDirectory = Path.Combine(directory.Path, "credentials", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(credentialDirectory);
        File.WriteAllText(Path.Combine(credentialDirectory, "config.json"), "live-private-credential");
        using var lease = new FileStream(Path.Combine(directory.Path, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var runner = new FakeRunner(); var result = await Executor(directory.Path, runner).ExecuteAsync(Request());
        Assert.Equal("agent_busy", result.Code); Assert.Empty(runner.Requests);
        Assert.True(File.Exists(Path.Combine(credentialDirectory, "config.json")));
    }

    [Fact]
    public async Task UnexpectedFilesInCredentialStorageAreNotSilentlyDeleted()
    {
        using var directory = new AgentTestDirectory(); var credentials = Path.Combine(directory.Path, "credentials");
        Directory.CreateDirectory(credentials); var unexpected = Path.Combine(credentials, "user-owned-file");
        File.WriteAllText(unexpected, "do not delete");
        var runner = new FakeRunner();
        Assert.Equal("credential_cleanup_failed", (await Executor(directory.Path, runner).ExecuteAsync(Request())).Code);
        Assert.Equal("do not delete", File.ReadAllText(unexpected)); Assert.Empty(runner.Requests);
    }
}

internal static class ArgumentListTestExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string item) => values.ToList().IndexOf(item);
}
