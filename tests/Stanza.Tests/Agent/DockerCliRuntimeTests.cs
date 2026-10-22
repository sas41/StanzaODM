using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;

namespace Stanza.Tests.Agent;

public sealed class DockerCliRuntimeTests
{
    [Theory]
    [InlineData(ComposeOperation.Validate, "config", "--quiet")]
    [InlineData(ComposeOperation.Build, "build", null)]
    [InlineData(ComposeOperation.Pull, "pull", null)]
    [InlineData(ComposeOperation.Up, "up", "--detach")]
    [InlineData(ComposeOperation.Down, "down", null)]
    [InlineData(ComposeOperation.Start, "start", null)]
    [InlineData(ComposeOperation.Stop, "stop", null)]
    [InlineData(ComposeOperation.Restart, "restart", null)]
    [InlineData(ComposeOperation.Ps, "ps", "--all")]
    [InlineData(ComposeOperation.Logs, "logs", "--no-color")]
    public async Task AllowedOperationsUseExplicitArgumentsAndIsolatedEnvironment(ComposeOperation operation, string command, string? option)
    {
        using var fixture = new Fixture();
        var result = await fixture.Runtime.ExecuteAsync(fixture.Request(operation));
        Assert.True(result.Succeeded);
        var invocation = Assert.Single(fixture.Runner.Requests);
        Assert.Equal("/usr/bin/docker", invocation.Executable);
        Assert.Equal(fixture.Workspace, invocation.WorkingDirectory);
        Assert.Equal("compose", invocation.Arguments[0]);
        Assert.Contains(command, invocation.Arguments);
        if (option is not null) { Assert.Contains(option, invocation.Arguments); }
        Assert.Contains("--env-file", invocation.Arguments);
        Assert.Contains(Path.Combine(fixture.Workspace, "resolved.env"), invocation.Arguments);
        Assert.Contains(Path.Combine(fixture.Workspace, "compose.yml"), invocation.Arguments);
        Assert.Equal("deployment-1", invocation.Arguments[Array.IndexOf(invocation.Arguments.ToArray(), "--project-name") + 1]);
        Assert.Equal(new[] { "DOCKER_CONFIG", "DOCKER_HOST", "HOME", "LANG", "LC_ALL", "PATH" }, invocation.Environment.Keys.Order().ToArray());
        Assert.DoesNotContain("--volumes", invocation.Arguments);
        Assert.DoesNotContain("--rmi", invocation.Arguments);
        Assert.DoesNotContain("--follow", invocation.Arguments);
        Assert.Equal(operation is ComposeOperation.Logs or ComposeOperation.Ps ? "sensitive output" : null, result.SensitiveOutput);
        Assert.DoesNotContain("sensitive output", result.ToString());
    }

    [Fact]
    public async Task ExecutionDefaultsToUnavailableWithoutStartingProcess()
    {
        using var fixture = new Fixture(false);
        var result = await fixture.Runtime.ExecuteAsync(fixture.Request());
        Assert.Equal("execution_disabled", result.Code);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task ArgumentInjectionAndUnknownOperationsAreRejected()
    {
        using var fixture = new Fixture();
        foreach (var request in new[]
        {
            fixture.Request() with { ProjectName = "--volumes" },
            fixture.Request() with { ProjectName = "test; touch /tmp/pwned" },
            fixture.Request() with { Services = ["--volumes"] },
            fixture.Request() with { Services = ["web; rm -rf /"] },
            fixture.Request() with { Operation = (ComposeOperation)999 }
        })
        {
            Assert.Equal("invalid_compose_request", (await fixture.Runtime.ExecuteAsync(request)).Code);
        }
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../../outside")]
    [InlineData("sub\\file.yml")]
    [InlineData("./compose.yml")]
    public async Task RejectsComposeFileEscapes(string composeFile)
    {
        using var fixture = new Fixture();
        Assert.False((await fixture.Runtime.ExecuteAsync(fixture.Request() with { ComposeFile = composeFile })).Succeeded);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData("../workspace-1")]
    [InlineData("/etc")]
    [InlineData("--workspace")]
    public async Task RejectsWorkspaceEscapes(string workspaceId)
    {
        using var fixture = new Fixture();
        Assert.Equal("invalid_workspace", (await fixture.Runtime.ExecuteAsync(fixture.Request() with { WorkspaceId = workspaceId })).Code);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RejectsFileAndWorkspaceSymlinks()
    {
        using var fixture = new Fixture();
        File.CreateSymbolicLink(Path.Combine(fixture.Workspace, "linked.yml"), Path.Combine(fixture.Workspace, "compose.yml"));
        Directory.CreateSymbolicLink(Path.Combine(fixture.Directory.Path, "workspaces", "linked"), fixture.Workspace);
        Assert.False((await fixture.Runtime.ExecuteAsync(fixture.Request() with { ComposeFile = "linked.yml" })).Succeeded);
        Assert.False((await fixture.Runtime.ExecuteAsync(fixture.Request() with { WorkspaceId = "linked" })).Succeeded);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task FilenamesWithShellSyntaxRemainSingleArguments()
    {
        using var fixture = new Fixture();
        const string filename = "compose; touch pwned.yml";
        File.WriteAllText(Path.Combine(fixture.Workspace, filename), "services: {}");
        Assert.True((await fixture.Runtime.ExecuteAsync(fixture.Request() with { ComposeFile = filename })).Succeeded);
        Assert.Contains(Path.Combine(fixture.Workspace, filename), Assert.Single(fixture.Runner.Requests).Arguments);
    }

    [Theory]
    [InlineData(ComposeOperation.Down)]
    [InlineData(ComposeOperation.Validate)]
    public async Task UnsupportedServiceSelectionDoesNotRun(ComposeOperation operation)
    {
        using var fixture = new Fixture();
        Assert.Equal("service_selection_unavailable", (await fixture.Runtime.ExecuteAsync(fixture.Request(operation) with { Services = ["web"] })).Code);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task SupportedServiceSelectionIsStructured()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Runtime.ExecuteAsync(fixture.Request(ComposeOperation.Start) with { Services = ["web", "database"] })).Succeeded);
        Assert.Equal(new[] { "web", "database" }, Assert.Single(fixture.Runner.Requests).Arguments.TakeLast(2));
    }

    [Theory]
    [InlineData(1, false, false, "compose_failed")]
    [InlineData(0, true, false, "runtime_output_limit")]
    [InlineData(0, false, true, "runtime_timeout")]
    public async Task ErrorsNeverExposeCommandOutput(int exitCode, bool truncated, bool timedOut, string code)
    {
        using var fixture = new Fixture();
        fixture.Runner.Result = new(exitCode, "secret token", "secret password", truncated, timedOut);
        var result = await fixture.Runtime.ExecuteAsync(fixture.Request(ComposeOperation.Logs));
        Assert.Equal(code, result.Code);
        Assert.Null(result.SensitiveOutput);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public async Task DetectsDockerDaemonAndComposeSeparately()
    {
        using var fixture = new Fixture();
        fixture.Runner.Results.Enqueue(new(0, "29.0.1\n", "", false, false));
        fixture.Runner.Results.Enqueue(new(0, "v2.40.0\n", "", false, false));
        var capabilities = await fixture.Runtime.DetectAsync();
        Assert.True(capabilities.DockerAvailable);
        Assert.True(capabilities.ComposeAvailable);
        Assert.True(capabilities.ExecutionEnabled);
        Assert.False(capabilities.CommandChannelAvailable);
        Assert.False(capabilities.HostExecutionAvailable);
        Assert.False(capabilities.TerminalAvailable);
        Assert.Equal("29.0.1", capabilities.DockerVersion);
        Assert.Equal("v2.40.0", capabilities.ComposeVersion);
        Assert.Equal(new[] { "version", "--format", "{{.Server.Version}}" }, fixture.Runner.Requests[0].Arguments);
        Assert.Equal(new[] { "compose", "version", "--short" }, fixture.Runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task MissingDaemonAndSecretDiagnosticsDoNotBecomeCapabilities()
    {
        using var fixture = new Fixture();
        fixture.Runner.Results.Enqueue(new(1, "secret", "secret", false, false));
        fixture.Runner.Results.Enqueue(new(0, "secret token = abc", "", false, false));
        var capabilities = await fixture.Runtime.DetectAsync();
        Assert.False(capabilities.DockerAvailable);
        Assert.False(capabilities.ComposeAvailable);
        Assert.False(capabilities.ExecutionEnabled);
        Assert.Null(capabilities.DockerVersion);
        Assert.Null(capabilities.ComposeVersion);
        Assert.DoesNotContain("secret", capabilities.ToString());
    }

    [Fact]
    public async Task EnvironmentFileIsRequiredAndMustRemainInsideWorkspace()
    {
        using var fixture = new Fixture();
        Assert.False((await fixture.Runtime.ExecuteAsync(fixture.Request() with { EnvironmentFile = "../secret.env" })).Succeeded);
        Assert.False((await fixture.Runtime.ExecuteAsync(fixture.Request() with { EnvironmentFile = "missing.env" })).Succeeded);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task CallerCancellationIsPassedToRunner()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Runtime.ExecuteAsync(fixture.Request(), cancellation.Token));
        Assert.Empty(fixture.Runner.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        public AgentTestDirectory Directory { get; } = new();
        public string Workspace { get; }
        public FakeProcessRunner Runner { get; } = new();
        public DockerCliRuntime Runtime { get; }
        public Fixture(bool enabled = true)
        {
            Workspace = Path.Combine(Directory.Path, "workspaces", "workspace-1");
            System.IO.Directory.CreateDirectory(Workspace);
            File.WriteAllText(Path.Combine(Workspace, "compose.yml"), "services: {}");
            File.WriteAllText(Path.Combine(Workspace, "resolved.env"), "");
            Runtime = new(Runner, Options.Create(new AgentOptions { PersistentDataRoot = Directory.Path }),
                Options.Create(new DockerRuntimeOptions { EnableExecution = enabled }));
        }
        public ComposeRequest Request(ComposeOperation operation = ComposeOperation.Validate) =>
            new(operation, "workspace-1", "compose.yml", "deployment-1", "resolved.env");
        public void Dispose() => Directory.Dispose();
    }

    internal sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Queue<ProcessResult> Results { get; } = new();
        public ProcessResult Result { get; set; } = new(0, "sensitive output", "", false, false);
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Result);
        }
    }
}
