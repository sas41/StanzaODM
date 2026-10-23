using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Agent.Terminals;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;
using Stanza.Tests.Enrollment;

namespace Stanza.Tests.AgentLogs;

public sealed class AgentLogChannelIntegrationTests
{
    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task SignedAgentChannelForwardsLiveLogsAndDetachNeverEntersWorkLedgerOrExec()
    {
        await using var store = await EnrollmentTestStore.CreateAsync();
        var credential = (await store.IssueAsync()).Token;
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        await store.RegisterAsync(identity.CreateEnrollmentRequest(credential, "logs-test", store.Clock.GetUtcNow()));
        var agent = (await store.ListAsync()).Single().Id;
        await store.SetStatusAsync(agent, AgentEnrollmentProtocol.Approved);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(store.Clock); builder.Services.AddSingleton(store.Protection);
        builder.Services.AddScoped(_ => store.CreateContext()); builder.Services.AddScoped<AgentAdministrationService>();
        builder.Services.AddStanzaAgentTransport();
        await using var app = builder.Build(); app.UseWebSockets(); app.MapStanzaAgentTransport(); await app.StartAsync();
        var broker = app.Services.GetRequiredService<AgentCommandBroker>();
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path, ControlPlaneUrl = app.Urls.Single(), EnrollmentToken = credential, AllowInsecureHttp = true });
        var dockerOptions = Options.Create(new DockerRuntimeOptions());
        var runner = new ProcessRunner(TimeProvider.System);
        var runtime = new DockerCliRuntime(runner, options, dockerOptions);
        var executor = new RejectingExecutor();
        var channel = new AgentCommandChannel(options, new AgentState(), executor, store.Clock, new AgentTerminalManager(), runtime,
            dockerOptions, Options.Create(new HostExecutionOptions()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var deployment = Guid.NewGuid(); var name = "stanza-log-wire-" + deployment.ToString("N");
        async Task<string> Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", "/tmp/opencode", arguments,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(90)));
            Assert.True(result.ExitCode == 0, result.StandardError); return result.StandardOutput;
        }
        Task? running = null;
        try
        {
            await Docker("run", "--detach", "--name", name, "--label", "com.docker.compose.project=stanza-" + deployment.ToString("N"), "alpine:3.22",
                "/bin/sh", "-c", "printf 'stdout-1\\n'; printf 'stderr-1\\n' >&2; sleep 2; printf 'stdout-2\\n'; printf 'stderr-2\\n' >&2; exec sleep 180");
            running = channel.RunAsync(identity, cancellation.Token);
            while (broker.Capabilities(agent) is null) await Task.Delay(10, cancellation.Token);
            await using var logs = await broker.OpenTerminalAsync(agent, deployment,
                new("ignored", [], Container: name, Mode: "logs"), new(TimeoutSeconds: 60), cancellation.Token);
            await Assert.ThrowsAsync<IOException>(() => logs.WriteInputAsync("kill 1\n"u8.ToArray(), cancellation.Token).AsTask());
            await Assert.ThrowsAsync<IOException>(() => logs.ResizeAsync(100, 30, cancellation.Token).AsTask());
            var text = new StringBuilder();
            while (!new[] { "stdout-1", "stderr-1", "stdout-2", "stderr-2" }.All(marker => text.ToString().Contains(marker, StringComparison.Ordinal)))
                text.Append(Encoding.UTF8.GetString((await logs.ReadOutputAsync(cancellation.Token)).Data.Span));
            Assert.Contains("stdout-1", text.ToString()); Assert.Contains("stderr-1", text.ToString()); Assert.Contains("stdout-2", text.ToString());
            Assert.DoesNotContain('\0', text.ToString());
            await logs.CloseAsync(cancellation.Token);
            Assert.True(broker.IsConnected(agent)); Assert.Equal(0, executor.Calls);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Path, "workspaces")));
            Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            Assert.Equal("null", (await Docker("inspect", "--format", "{{json .ExecIDs}}", name)).Trim());
        }
        finally
        {
            cancellation.Cancel();
            if (running is not null) try { await running; } catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
            await Docker("rm", "--force", "--", name); await app.StopAsync();
        }
    }
    private sealed class RejectingExecutor : IAgentWorkExecutor
    {
        public int Calls { get; private set; }
        public Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException("Streaming must not execute durable commands."); }
    }
}
