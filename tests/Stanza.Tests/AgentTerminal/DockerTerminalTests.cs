using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Stanza.Agent.Runtime;
using Stanza.Agent.Terminals;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;

namespace Stanza.Tests.AgentTerminal;

public sealed class DockerTerminalTests
{
    [Theory]
    [InlineData("tcp://localhost:2375")]
    [InlineData("unix://remote/socket")]
    [InlineData("unix:///")]
    [InlineData("unix:///var/run/docker.sock?x=1")]
    public void RejectsInsecureAndAmbiguousEndpoints(string endpoint) => Assert.Throws<IOException>(() => new DockerTerminalClient(endpoint));

    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/bar")]
    [InlineData("-option")]
    [InlineData("a?b")]
    [InlineData("a\r\n")]
    public void DockerIdentifiersCannotEscapeRoutes(string id) => Assert.False(DockerTerminalClient.SafeId(id));

    [Theory]
    [InlineData("HTTP/1.1 101 UPGRADED\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Type: application/vnd.docker.raw-stream\r\n\r\n")]
    public async Task UpgradeLeavesFirstTtyByteUntouched(string header)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(header + "X"));
        await DockerTerminalClient.ReadUpgradeAsync(input, default);
        Assert.Equal('X', input.ReadByte());
    }

    [Theory]
    [InlineData("HTTP/1.1 500 Server Error\r\n\r\n")]
    [InlineData("HTTP/1.1 101 UPGRADED\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 101 UPGRADED\r\nConnection: Upgrade\r\nUpgrade: tcp\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("HTTP/1.1 101 UPGRADED\r\nConnection: Upgrade\r\nUpgrade: tcp\r\nUpgrade: tcp\r\n\r\n")]
    [InlineData("HTTP/1.1 101 UPGRADED\r\nInvalid\r\n\r\n")]
    [InlineData("HTTP/1.1 101 UPGRADED\r\n")]
    public async Task RejectsMalformedOrFramedUpgrade(string header)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(header));
        await Assert.ThrowsAsync<IOException>(() => DockerTerminalClient.ReadUpgradeAsync(input, default));
    }

    [Fact]
    public async Task RejectsUnboundedUpgradeHeader()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 17000)));
        await Assert.ThrowsAsync<IOException>(() => DockerTerminalClient.ReadUpgradeAsync(input, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeUnixSocketRejectsForeignLabelAndHttpErrors(bool httpError)
    {
        var socketPath = Path.Combine("/tmp/opencode", "stanza-terminal-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath)); listener.Listen(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptAsync(timeout.Token);
            await using var stream = new NetworkStream(connection);
            var bytes = new byte[4096];
            var count = await stream.ReadAsync(bytes, timeout.Token);
            Assert.StartsWith("GET /containers/safe-name/json ", Encoding.ASCII.GetString(bytes, 0, count));
            var body = "{\"Id\":\"safe-name\",\"State\":{\"Running\":true},\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"foreign\"}}}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(httpError ? "403 Forbidden" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
        });
        try
        {
            using var client = new DockerTerminalClient("unix://" + socketPath);
            await Assert.ThrowsAsync<IOException>(() => client.ResolveAsync(Guid.NewGuid(), new("/bin/sh", [], Container: "safe-name"), timeout.Token));
            await server;
        }
        finally { File.Delete(socketPath); }
    }

    [Fact]
    public async Task HostTargetsAndDisabledPoliciesNeverCreateTerminal()
    {
        var manager = new AgentTerminalManager();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new("terminal.open", id,
            new(id, Guid.NewGuid(), "terminal.open", [], [], new("/bin/sh", []), Policy: new(ExecutionEnabled: false))), _ => Task.CompletedTask, default));
        using var client = new DockerTerminalClient("unix:///var/run/docker.sock");
        await Assert.ThrowsAsync<IOException>(() => client.ResolveAsync(Guid.NewGuid(), new("/bin/sh", []), default));
    }

    [Fact]
    public async Task AgentSessionAndInputQueuesAreBoundedAndDisconnectReleasesAllPendingOpens()
    {
        var socketPath = Path.Combine("/tmp/opencode", "stanza-terminal-bound-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath)); listener.Listen(64);
        var manager = new AgentTerminalManager();
        var sent = new ConcurrentQueue<AgentWireMessage>();
        Task Send(AgentWireMessage message) { sent.Enqueue(message); return Task.CompletedTask; }
        var ids = Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToArray();
        try
        {
            foreach (var id in ids)
                await manager.HandleAsync(new("terminal.open", id, new(id, Guid.NewGuid(), "terminal.open", [], [],
                    new("/bin/sh", [], Container: "scoped"), Policy: new(DockerEndpoint: "unix://" + socketPath))), Send, default);
            var rejected = Assert.Single(sent); Assert.Equal(ids[^1], rejected.Id); Assert.Equal("125", rejected.Data);
            await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new("terminal.input", ids[0], Data: new string('A', 87385)), Send, default));
            await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new("terminal.resize", ids[0], Data: "[501,20]"), Send, default));
            for (var i = 0; i < 17; i++) await manager.HandleAsync(new("terminal.input", ids[0], Data: "YQ=="), Send, default);
            await manager.CloseAllAsync();
            Assert.Equal(17, sent.Count); Assert.All(sent, message => Assert.Equal("terminal.exited", message.Type));
        }
        finally { await manager.CloseAllAsync(); File.Delete(socketPath); }
    }

    [DockerIntegrationFact]
    [Trait("Category", "DockerIntegration")]
    public async Task RealAlpinePtyInputOutputResizeExitCloseAndDeploymentIsolation()
    {
        var deployment = Guid.NewGuid();
        var name = "stanza-pty-test-" + deployment.ToString("N");
        var runner = new ProcessRunner(TimeProvider.System);
        async Task<string> Docker(params string[] arguments)
        {
            var result = await runner.RunAsync(new("/usr/bin/docker", "/tmp/opencode", arguments,
                new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOCKER_HOST"] = "unix:///var/run/docker.sock" }, TimeSpan.FromSeconds(90)));
            Assert.Equal(0, result.ExitCode); return result.StandardOutput;
        }
        var manager = new AgentTerminalManager();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var frames = Channel.CreateUnbounded<AgentWireMessage>();
        Task Send(AgentWireMessage message) { Assert.True(frames.Writer.TryWrite(message)); return Task.CompletedTask; }
        async Task<AgentWireMessage> Read() => await frames.Reader.ReadAsync(timeout.Token);
        async Task<Guid> Open(AgentExecution execution)
        {
            var id = Guid.NewGuid();
            await manager.HandleAsync(new("terminal.open", id, new(id, deployment, "terminal.open", [], [], execution, Policy: new(TimeoutSeconds: 90))), Send, timeout.Token);
            var opened = await Read(); Assert.Equal(id, opened.Id); Assert.Equal("terminal.opened", opened.Type); return id;
        }
        async Task<string> Until(string marker)
        {
            var text = new StringBuilder();
            while (!text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var frame = await Read(); Assert.Equal("terminal.output", frame.Type);
                text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(frame.Data!)));
            }
            return text.ToString();
        }
        try
        {
            await Docker("run", "--detach", "--name", name, "--label", "com.docker.compose.project=stanza-" + deployment.ToString("N"),
                "--label", "com.docker.compose.service=app", "alpine:3.22", "sleep", "300");
            using var api = new DockerTerminalClient("unix:///var/run/docker.sock");
            await Assert.ThrowsAsync<IOException>(() => api.ResolveAsync(Guid.NewGuid(), new("/bin/sh", [], Container: name), timeout.Token));
            var id = await Open(new("/bin/sh", ["-c", "tty; printf 'READY\\n'; read line; printf 'VALUE:%s\\n' \"$line\"; stty size; exit 7"], Service: "app"));
            Assert.Contains("/dev/pts/", await Until("READY"));
            await manager.HandleAsync(new("terminal.resize", id, Data: "[101,37]"), Send, timeout.Token);
            await manager.HandleAsync(new("terminal.input", id, Data: Convert.ToBase64String(Encoding.UTF8.GetBytes("literal;$(not-shell)\n"))), Send, timeout.Token);
            var output = new StringBuilder(); AgentWireMessage frame;
            while ((frame = await Read()).Type != "terminal.exited") output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(frame.Data!)));
            Assert.Equal("7", frame.Data); Assert.Contains("VALUE:literal;$(not-shell)", output.ToString()); Assert.Contains("37 101", output.ToString());
            id = await Open(new("/bin/sh", ["-c", "echo $$ > /tmp/stanza-terminal-pid; printf 'RUNNING\\n'; exec sleep 180"], Container: name));
            await Until("RUNNING");
            var pid = (await Docker("exec", name, "cat", "/tmp/stanza-terminal-pid")).Trim();
            await manager.HandleAsync(new("terminal.close", id), Send, timeout.Token);
            Assert.Equal("terminal.exited", (await Read()).Type);
            Assert.Contains("gone", await Docker("exec", name, "/bin/sh", "-c", "if kill -0 \"$1\" 2>/dev/null; then echo alive; else echo gone; fi", "probe", pid));
            Assert.Equal("true", (await Docker("inspect", "--format", "{{.State.Running}}", name)).Trim());
        }
        finally
        {
            await manager.CloseAllAsync();
            await Docker("rm", "--force", "--", name);
        }
    }
}
