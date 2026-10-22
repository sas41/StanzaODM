using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Stanza.Agent.Terminals;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Terminals;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.Tests.AgentLogs;

public sealed class LogBrokerTests
{
    [Fact]
    public async Task LogsUseSameWireAndBrokerRejectsMutationWithoutSendingInputOrResize()
    {
        await using var wire = await LogWire.CreateAsync();
        var session = await wire.OpenAsync();
        await Assert.ThrowsAsync<IOException>(() => session.WriteInputAsync(Encoding.UTF8.GetBytes("kill 1\n"), wire.Token).AsTask());
        await Assert.ThrowsAsync<IOException>(() => session.ResizeAsync(100, 30, wire.Token).AsTask());
        await wire.SendAsync(new("terminal.output", wire.LastSession, Data: Convert.ToBase64String(Encoding.UTF8.GetBytes("stdout\nstderr\n"))));
        Assert.Equal("stdout\nstderr\n", Encoding.UTF8.GetString((await session.ReadOutputAsync(wire.Token)).Data.Span));
        var workRequest = new AgentWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "status", [], []);
        var work = wire.Broker.DispatchAsync(wire.Agent, workRequest, wire.Token);
        var frame = await wire.ReadAsync(); Assert.Equal("work.request", frame.Type);
        await wire.SendAsync(new("work.response", frame.Id, Response: new(frame.Id, true, "succeeded")));
        Assert.True((await work).Succeeded);
        await wire.SendAsync(new("terminal.exited", wire.LastSession, Data: "0"));
        Assert.Equal(0, (await session.ReadOutputAsync(wire.Token)).ExitCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Sustained64KiBOutputClosesOnlySlowLogSubscriberAndRetainsBoundedQueue()
    {
        await using var wire = await LogWire.CreateAsync();
        var session = await wire.OpenAsync();
        var payload = Convert.ToBase64String(Enumerable.Repeat((byte)251, 65536).ToArray());
        for (var i = 0; i < 128; i++) await wire.SendAsync(new("terminal.output", wire.LastSession, Data: payload));
        Assert.Equal("terminal.close", (await wire.ReadAsync()).Type);
        await wire.SendAsync(new("terminal.exited", wire.LastSession, Data: "125"));
        for (var i = 0; i < 16; i++) Assert.Equal(65536, (await session.ReadOutputAsync(wire.Token)).Data.Length);
        try { Assert.Equal(125, (await session.ReadOutputAsync(wire.Token)).ExitCode); }
        catch (IOException) { }
        await Assert.ThrowsAsync<IOException>(() => session.ReadOutputAsync(wire.Token).AsTask());
        Assert.True(wire.Broker.IsConnected(wire.Agent));
        // A later open is independent: slow-log cleanup did not abort the Agent channel or replay a command.
        var later = await wire.OpenAsync();
        var closing = later.CloseAsync(wire.Token).AsTask();
        var close = await wire.ReadAsync(); Assert.Equal("terminal.close", close.Type);
        await wire.SendAsync(new("terminal.exited", close.Id, Data: "125")); await closing;
    }

    [Theory]
    [InlineData("host")]
    [InlineData("LOGS")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnknownModeFailsClosedOnBothSides(string? mode)
    {
        await using var wire = await LogWire.CreateAsync();
        await Assert.ThrowsAsync<IOException>(() => wire.Broker.OpenTerminalAsync(wire.Agent, Guid.NewGuid(),
            new("/bin/sh", [], Container: "scoped", Mode: mode!), new(), wire.Token));
        var manager = new AgentTerminalManager();
        var id = Guid.NewGuid(); var sent = 0;
        await Assert.ThrowsAsync<IOException>(() => manager.HandleAsync(new("terminal.open", id,
            new(id, Guid.NewGuid(), "terminal.open", [], [], new("/bin/sh", [], Container: "scoped", Mode: mode!), Policy: new())),
            _ => { sent++; return Task.CompletedTask; }, wire.Token));
        Assert.Equal(0, sent);
    }

    private sealed class LogWire : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public AgentCommandBroker Broker { get; } = new();
        public Guid Agent { get; } = Guid.NewGuid();
        public Guid LastSession { get; private set; }
        private AgentCommandSession session = null!;
        private WebSocket client = null!;
        private WebSocket server = null!;
        private Task running = null!;
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
        public CancellationToken Token => lifetime.Token;
        public static async Task<LogWire> CreateAsync()
        {
            var wire = new LogWire();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var connecting = new TcpClient();
            var connect = connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync(); await connect;
            wire.client = WebSocket.CreateFromStream(connecting.GetStream(), false, null, Timeout.InfiniteTimeSpan);
            wire.server = WebSocket.CreateFromStream(accepted.GetStream(), true, null, Timeout.InfiniteTimeSpan);
            wire.session = wire.Broker.Reserve(wire.Agent)!;
            wire.running = wire.session.RunAsync(wire.server, _ => Task.FromResult(true), wire.Token);
            Assert.Equal("hello", (await wire.ReadAsync()).Type);
            await wire.SendAsync(new("capabilities", Guid.Empty, Data: JsonSerializer.Serialize(new AgentCapabilities("docker", true, true, "29.0", "2.0", "x64", false, true))));
            while (wire.Broker.Capabilities(wire.Agent) is null) await Task.Delay(5, wire.Token);
            return wire;
        }
        public async Task<IAgentTerminalSession> OpenAsync()
        {
            var opening = Broker.OpenTerminalAsync(Agent, Guid.NewGuid(), new("ignored", [], Container: "scoped", Mode: "logs"), new(), Token);
            var open = await ReadAsync(); Assert.Equal("terminal.open", open.Type); Assert.Equal("logs", open.Request!.Execution!.Mode);
            LastSession = open.Id; await SendAsync(new("terminal.opened", open.Id)); return await opening;
        }
        public async Task SendAsync(AgentWireMessage message) => await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, WireJson), WebSocketMessageType.Text, true, Token);
        public async Task<AgentWireMessage> ReadAsync()
        {
            while (true)
            {
                var frame = await AgentCommandChannel.ReceiveAsync(client, Token) ?? throw new IOException("Agent disconnected.");
                if (frame.Type != "ping") return frame;
                await SendAsync(new("pong", frame.Id));
            }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); client.Abort(); server.Abort(); await running;
            Broker.Release(Agent, session); client.Dispose(); server.Dispose(); lifetime.Dispose();
        }
    }
}
