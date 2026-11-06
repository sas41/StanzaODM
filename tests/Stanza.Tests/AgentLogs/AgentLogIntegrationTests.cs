using System.Text;
using System.Threading.Channels;
using Stanza.Agent.Runtime;
using Stanza.Agent.Logs;
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
        var manager = new AgentLogManager();
        var frames = Channel.CreateUnbounded<AgentWireMessage>();
        Task Send(AgentWireMessage message) { Assert.True(frames.Writer.TryWrite(message)); return Task.CompletedTask; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        async Task<AgentWireMessage> Read() => await frames.Reader.ReadAsync(timeout.Token);
        async Task<Guid> Open(Guid scope, string? service = null)
        {
            var id = Guid.NewGuid();
            await manager.HandleAsync(new("log.open", id, new(id, scope, "log.open", [], [],
                new("logs", [], Service: service, Container: service is null ? name : null, Mode: "logs"),
                Policy: new(TimeoutSeconds: 60))), Send, timeout.Token);
            return id;
        }
        async Task<string> Until(params string[] markers)
        {
            var output = new StringBuilder();
            while (!markers.All(marker => output.ToString().Contains(marker, StringComparison.Ordinal)))
            {
                var message = await Read(); Assert.Equal("log.output", message.Type);
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
            var denied = await Read(); Assert.Equal("log.ended", denied.Type); Assert.Equal("125", denied.Data);
            var id = await Open(deployment);
            Assert.Equal("log.opened", (await Read()).Type);
            var live = await Until("stdout-1", "stderr-1", "stdout-3", "stderr-3");
            Assert.Contains("stdout-1", live); Assert.Contains("stderr-1", live); Assert.Contains("stdout-3", live);
            Assert.DoesNotContain('\0', live); Assert.DoesNotContain('\u0001', live); Assert.DoesNotContain('\u0002', live);
            Assert.Matches(@"\d{4}-\d{2}-\d{2}T", live);
            var initialProcesses = (await Docker("top", name, "-eo", "pid,comm")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            await manager.HandleAsync(new("log.close", id), Send, timeout.Token);
            var closed = await Read(); Assert.Equal("log.ended", closed.Type); Assert.Equal(id, closed.Id);
            Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            Assert.Equal(initialProcesses, (await Docker("top", name, "-eo", "pid,comm")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("null", (await Docker("inspect", "--format", "{{json .ExecIDs}}", name)).Trim());

            // Defense in depth: Agent rejects controls even if a malicious peer bypasses the read-only CP adapter.
            foreach (var type in new[] { "terminal.input", "terminal.resize" })
            {
                await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new(type, Guid.NewGuid(), Data: "unsupported"), Send, timeout.Token));
                Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
            }
            Assert.Equal("0", (await Docker("wait", name)).Trim());
            id = await Open(deployment, "app"); // Service lookup must include stopped containers (all=1).
            Assert.Equal("log.opened", (await Read()).Type);
            var tail = new StringBuilder(); AgentWireMessage end;
            while ((end = await Read()).Type == "log.output") tail.Append(Encoding.UTF8.GetString(Convert.FromBase64String(end.Data!)));
            Assert.Equal("log.ended", end.Type); Assert.Equal("0", end.Data); Assert.Equal(id, end.Id);
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
            using var client = new DockerLogClient("unix:///var/run/docker.sock");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var target = await client.ResolveTargetAsync(deployment, new("logs", [], Container: name, Mode: "logs"), timeout.Token);
            Assert.True(target.Tty);
            await using var logs = await client.OpenLogsAsync(target, timeout.Token);
            var text = await DockerLogStreamTests.ReadAllAsync(logs);
            Assert.Contains("tty-stdout", text); Assert.Contains("tty-stderr", text); Assert.DoesNotContain('\0', text);
        }
        finally { await Docker("rm", "--force", "--", name); }
    }
}
