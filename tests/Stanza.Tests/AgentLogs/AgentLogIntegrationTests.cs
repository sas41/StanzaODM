using System.Text;
using System.Threading.Channels;
using Stanza.Agent.Runtime;
using Stanza.Agent.Terminals;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;

namespace Stanza.Tests.AgentLogs;

public sealed class AgentLogIntegrationTests
{
    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task FollowsStdoutAndStderrThenReadsStoppedTailWithoutExecAndCloseLeavesWorkloadUntouched()
    {
        var deployment = Guid.NewGuid(); var name = "stanza-log-test-" + deployment.ToString("N");
        var runner = new ProcessRunner(TimeProvider.System);
        async Task<string> Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", "/tmp/opencode", arguments,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(90)));
            Assert.True(result.ExitCode == 0, "Docker " + arguments[0] + ": " + result.StandardError); return result.StandardOutput;
        }
        var manager = new AgentTerminalManager();
        var frames = Channel.CreateUnbounded<AgentWireMessage>();
        Task Send(AgentWireMessage message) { Assert.True(frames.Writer.TryWrite(message)); return Task.CompletedTask; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        async Task<AgentWireMessage> Read() => await frames.Reader.ReadAsync(timeout.Token);
        async Task<Guid> Open(Guid scope, string? service = null)
        {
            var id = Guid.NewGuid();
            // Logs ignore execution fields. Even a deliberately invalid executable must never be invoked.
            await manager.HandleAsync(new("terminal.open", id, new(id, scope, "terminal.open", [], [],
                new("never-execute;$(unsafe)", ["ignored"], Service: service, Container: service is null ? name : null, Mode: "logs"),
                Policy: new(TimeoutSeconds: 60))), Send, timeout.Token);
            return id;
        }
        async Task<string> Until(params string[] markers)
        {
            var output = new StringBuilder();
            while (!markers.All(marker => output.ToString().Contains(marker, StringComparison.Ordinal)))
            {
                var message = await Read(); Assert.Equal("terminal.output", message.Type);
                output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(message.Data!)));
            }
            return output.ToString();
        }
        try
        {
            await Docker("run", "--detach", "--name", name, "--label", "com.docker.compose.project=stanza-" + deployment.ToString("N"),
                "--label", "com.docker.compose.service=app", "alpine:3.22", "/bin/sh", "-c",
                "i=1; while [ \"$i\" -le 8 ]; do printf 'stdout-%s\\n' \"$i\"; printf 'stderr-%s\\n' \"$i\" >&2; i=$((i+1)); sleep 1; done");
            await Open(Guid.NewGuid());
            var denied = await Read(); Assert.Equal("terminal.exited", denied.Type); Assert.Equal("125", denied.Data);
            var id = await Open(deployment);
            Assert.Equal("terminal.opened", (await Read()).Type);
            var live = await Until("stdout-1", "stderr-1", "stdout-3", "stderr-3");
            Assert.Contains("stdout-1", live); Assert.Contains("stderr-1", live); Assert.Contains("stdout-3", live);
            Assert.DoesNotContain('\0', live); Assert.DoesNotContain('\u0001', live); Assert.DoesNotContain('\u0002', live);
            Assert.Matches(@"\d{4}-\d{2}-\d{2}T", live);
            var initialProcesses = (await Docker("top", name, "-eo", "pid,comm")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            await manager.HandleAsync(new("terminal.close", id), Send, timeout.Token);
            var closed = await Read(); Assert.Equal("terminal.exited", closed.Type); Assert.Equal(id, closed.Id);
            Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            Assert.Equal(initialProcesses, (await Docker("top", name, "-eo", "pid,comm")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("null", (await Docker("inspect", "--format", "{{json .ExecIDs}}", name)).Trim());

            // Defense in depth: Agent rejects controls even if a malicious peer bypasses the read-only CP adapter.
            foreach (var type in new[] { "terminal.input", "terminal.resize" })
            {
                id = await Open(deployment); Assert.Equal("terminal.opened", (await Read()).Type);
                await manager.HandleAsync(new(type, id, Data: type == "terminal.input" ? "a2lsbCAxCg==" : "[100,30]"), Send, timeout.Token);
                AgentWireMessage message;
                do { message = await Read(); } while (message.Type == "terminal.output");
                Assert.Equal("terminal.exited", message.Type); Assert.Equal("125", message.Data);
                Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            }
            Assert.Equal("0", (await Docker("wait", name)).Trim());
            id = await Open(deployment, "app"); // Service lookup must include stopped containers (all=1).
            Assert.Equal("terminal.opened", (await Read()).Type);
            var tail = new StringBuilder(); AgentWireMessage end;
            while ((end = await Read()).Type == "terminal.output") tail.Append(Encoding.UTF8.GetString(Convert.FromBase64String(end.Data!)));
            Assert.Equal("terminal.exited", end.Type); Assert.Equal("0", end.Data); Assert.Equal(id, end.Id);
            Assert.Contains("stdout-8", tail.ToString()); Assert.Contains("stderr-8", tail.ToString());
            Assert.Equal("null", (await Docker("inspect", "--format", "{{json .ExecIDs}}", name)).Trim());
        }
        finally { await manager.CloseAllAsync(); await Docker("rm", "--force", "--", name); }
    }

    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task TtyContainerLogsAreReadAsRawBytes()
    {
        var deployment = Guid.NewGuid(); var name = "stanza-log-tty-" + deployment.ToString("N");
        var runner = new ProcessRunner(TimeProvider.System);
        async Task Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", "/tmp/opencode", arguments,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(90)));
            Assert.Equal(0, result.ExitCode);
        }
        try
        {
            await Docker("run", "--detach", "--tty", "--name", name, "--label", "com.docker.compose.project=stanza-" + deployment.ToString("N"),
                "alpine:3.22", "/bin/sh", "-c", "printf 'tty-stdout\\n'; printf 'tty-stderr\\n' >&2");
            await Docker("wait", name);
            using var client = new DockerTerminalClient("unix:///var/run/docker.sock");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var target = await client.ResolveTargetAsync(deployment, new("ignored", [], Container: name, Mode: "logs"), false, timeout.Token);
            Assert.True(target.Tty);
            await using var logs = await client.OpenLogsAsync(target, timeout.Token);
            var text = await DockerLogStreamTests.ReadAllAsync(logs);
            Assert.Contains("tty-stdout", text); Assert.Contains("tty-stderr", text); Assert.DoesNotContain('\0', text);
        }
        finally { await Docker("rm", "--force", "--", name); }
    }
}
