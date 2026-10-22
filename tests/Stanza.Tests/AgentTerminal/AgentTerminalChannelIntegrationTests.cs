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

namespace Stanza.Tests.AgentTerminal;

public sealed class AgentTerminalChannelIntegrationTests
{
    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task AuthenticatedAgentReportsActualCapabilitiesAndForwardsPtyAlongsideWorkThenDisconnectKillsOnlyExec()
    {
        await using var store = await EnrollmentTestStore.CreateAsync();
        var credential = (await store.IssueAsync()).Token;
        using var root = new AgentTestDirectory();
        using var identity = AgentIdentity.LoadOrCreate(root.Path);
        await store.RegisterAsync(identity.CreateEnrollmentRequest(credential, "terminal-test", store.Clock.GetUtcNow()));
        var agentId = (await store.ListAsync()).Single().Id;
        await store.SetStatusAsync(agentId, AgentEnrollmentProtocol.Approved);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(store.Clock); builder.Services.AddSingleton(store.Protection);
        builder.Services.AddScoped(_ => store.CreateContext()); builder.Services.AddScoped<AgentAdministrationService>();
        builder.Services.AddStanzaAgentTransport();
        await using var app = builder.Build(); app.UseWebSockets(); app.MapStanzaAgentTransport(); await app.StartAsync();
        var broker = app.Services.GetRequiredService<AgentCommandBroker>();
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path, ControlPlaneUrl = app.Urls.Single(), EnrollmentToken = credential, AllowLoopbackHttp = true });
        var dockerOptions = Options.Create(new DockerRuntimeOptions());
        var runner = new ProcessRunner(TimeProvider.System);
        var runtime = new DockerCliRuntime(runner, options, dockerOptions);
        var executor = new NoTerminalExecutor();
        var channel = new AgentCommandChannel(options, new AgentState(), executor, store.Clock,
            new AgentTerminalManager(), runtime, dockerOptions, Options.Create(new HostExecutionOptions()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var deployment = Guid.NewGuid(); var name = "stanza-wire-pty-" + deployment.ToString("N");
        async Task<string> Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", "/tmp/opencode", arguments,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(90)));
            Assert.Equal(0, result.ExitCode); return result.StandardOutput;
        }
        Task? running = null;
        try
        {
            await Docker("run", "--detach", "--name", name, "--label", "com.docker.compose.project=stanza-" + deployment.ToString("N"), "alpine:3.22", "sleep", "300");
            running = channel.RunAsync(identity, cancellation.Token);
            while (broker.Capabilities(agentId) is null) await Task.Delay(10, cancellation.Token);
            Assert.True(broker.Capabilities(agentId)!.TerminalAvailable);
            Assert.False(broker.Capabilities(agentId)!.HostExecutionAvailable);
            await using var terminal = await broker.OpenTerminalAsync(agentId, deployment,
                new("/bin/sh", ["-c", "echo $$ > /tmp/stanza-wire-pid; tty; read line; printf 'VALUE:%s\\n' \"$line\"; exec sleep 180"], Container: name), new(TimeoutSeconds: 60), cancellation.Token);
            var text = new StringBuilder();
            while (!text.ToString().Contains("/dev/pts/", StringComparison.Ordinal)) text.Append(Encoding.UTF8.GetString((await terminal.ReadOutputAsync(cancellation.Token)).Data.Span));
            var work = await broker.DispatchAsync(agentId, new(Guid.NewGuid(), deployment, "status", [], []), cancellation.Token);
            Assert.True(work.Succeeded); Assert.Equal(1, executor.Calls);
            await terminal.ResizeAsync(100, 30, cancellation.Token);
            await terminal.WriteInputAsync(Encoding.UTF8.GetBytes("literal;$(no-exec)\n"), cancellation.Token);
            text.Clear();
            while (!text.ToString().Contains("VALUE:literal;$(no-exec)", StringComparison.Ordinal)) text.Append(Encoding.UTF8.GetString((await terminal.ReadOutputAsync(cancellation.Token)).Data.Span));
            var pid = (await Docker("exec", name, "cat", "/tmp/stanza-wire-pid")).Trim();
            cancellation.Cancel();
            try { await running; } catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
            Assert.Contains("gone", await Docker("exec", name, "/bin/sh", "-c", "if kill -0 \"$1\" 2>/dev/null; then echo alive; else echo gone; fi", "probe", pid));
            Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            Assert.Equal(1, executor.Calls); // No terminal input or open entered the command ledger/executor.
        }
        finally
        {
            cancellation.Cancel();
            if (running is not null) try { await running; } catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
            await Docker("rm", "--force", "--", name);
            await app.StopAsync();
        }
    }
    private sealed class NoTerminalExecutor : IAgentWorkExecutor
    {
        public int Calls { get; private set; }
        public Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        { Calls++; Assert.DoesNotContain("terminal", request.Operation); return Task.FromResult(new AgentWorkResponse(request.Id, true, "succeeded")); }
    }
}
