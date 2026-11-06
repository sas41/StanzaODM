using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Runtime;

namespace Stanza.Tests.Agent;

public sealed class DockerCapabilitiesProbeTests
{
    [Fact]
    public async Task DetectsDaemonAndComposeWithoutCreatingAnotherWorkspaceOrExecutingCompose()
    {
        using var root = new AgentTestDirectory(); var runner = new Runner();
        runner.Results.Enqueue(new(0, "29.0.1\n", "", false, false));
        runner.Results.Enqueue(new(0, "v2.40.0\n", "", false, false));
        var probe = new DockerCapabilitiesProbe(runner, Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), Options.Create(new DockerRuntimeOptions()));
        var capabilities = await probe.DetectAsync();
        Assert.True(capabilities.DockerAvailable); Assert.True(capabilities.ComposeAvailable);
        Assert.Equal(OperatingSystem.IsLinux(), capabilities.LogsAvailable); Assert.False(capabilities.HostExecutionAvailable);
        Assert.Equal("29.0.1", capabilities.DockerVersion); Assert.Equal("v2.40.0", capabilities.ComposeVersion);
        Assert.Equal(["version", "--format", "{{.Server.Version}}"], runner.Requests[0].Arguments);
        Assert.Equal(["compose", "version", "--short"], runner.Requests[1].Arguments);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "workspaces")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "operation-workspaces")));
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal("unix:///var/run/docker.sock", request.Environment["DOCKER_HOST"]);
            Assert.DoesNotContain("DOTNET_ROOT", request.Environment.Keys); Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        });
    }

    [Fact]
    public async Task InvalidVersionsAndMissingDaemonAreNotReportedAsAvailableOrExposedAsDiagnostics()
    {
        using var root = new AgentTestDirectory(); var runner = new Runner();
        runner.Results.Enqueue(new(1, "private-error", "private-error", false, false));
        runner.Results.Enqueue(new(0, "secret token = abc", "", false, false));
        var probe = new DockerCapabilitiesProbe(runner, Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), Options.Create(new DockerRuntimeOptions()));
        var capabilities = await probe.DetectAsync();
        Assert.False(capabilities.DockerAvailable); Assert.False(capabilities.ComposeAvailable); Assert.False(capabilities.LogsAvailable);
        Assert.Null(capabilities.DockerVersion); Assert.Null(capabilities.ComposeVersion); Assert.DoesNotContain("secret", capabilities.ToString());
    }

    [Fact]
    public async Task InvalidEndpointAndCancellationNeverInvokeDocker()
    {
        using var root = new AgentTestDirectory(); var runner = new Runner();
        var options = Options.Create(new DockerRuntimeOptions { Endpoint = "tcp://host:2375" });
        var probe = new DockerCapabilitiesProbe(runner, Options.Create(new AgentOptions { PersistentDataRoot = root.Path }), options);
        Assert.False((await probe.DetectAsync()).DockerAvailable);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.DetectAsync(cancelled.Token)); Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("app-1", true)]
    [InlineData("0_app", true)]
    [InlineData("-option", false)]
    [InlineData("app\n", false)]
    [InlineData("app/other", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RuntimeIdentifierRulesRemainBounded(string? name, bool valid) => Assert.Equal(valid, DockerRuntimeOptions.IsIdentifier(name));

    private sealed class Runner : IProcessRunner
    {
        public readonly List<ProcessRequest> Requests = [];
        public readonly Queue<ProcessResult> Results = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Results.Dequeue()); }
    }
}
