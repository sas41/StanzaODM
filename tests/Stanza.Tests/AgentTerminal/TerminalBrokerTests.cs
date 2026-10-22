using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Terminals;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.Tests.AgentTerminal;

public sealed class TerminalBrokerTests
{
    [Fact]
    public async Task DuplexTerminalAndWorkRemainCorrelatedAndInputIsEphemeral()
    {
        await using var fixture = await WireFixture.CreateAsync();
        var session = await fixture.OpenAsync();
        await session.WriteInputAsync(Encoding.UTF8.GetBytes("secret-input"), fixture.Token);
        var input = await fixture.ReadAsync(); Assert.Equal("terminal.input", input.Type); Assert.Null(input.Request);
        Assert.Equal("secret-input", Encoding.UTF8.GetString(Convert.FromBase64String(input.Data!)));
        Assert.DoesNotContain("secret-input", input.ToString());
        await session.ResizeAsync(120, 40, fixture.Token);
        Assert.Equal("[120,40]", (await fixture.ReadAsync()).Data);
        var request = new AgentWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "status", [], []);
        var work = fixture.Broker.DispatchAsync(fixture.Agent, request, fixture.Token);
        Assert.Equal("work.request", (await fixture.ReadAsync()).Type);
        await fixture.SendAsync(new("terminal.output", Guid.NewGuid(), Data: "aWdub3JlZA==")); // unknown session cannot leak into this reader
        await fixture.SendAsync(new("terminal.output", fixture.LastSession, Data: "b3V0cHV0"));
        Assert.Equal("output", Encoding.UTF8.GetString((await session.ReadOutputAsync(fixture.Token)).Data.Span));
        await fixture.SendAsync(new("work.response", request.Id, Response: new(request.Id, true, "succeeded")));
        Assert.True((await work).Succeeded);
        await fixture.SendAsync(new("terminal.exited", fixture.LastSession, Data: "23"));
        Assert.Equal(23, (await session.ReadOutputAsync(fixture.Token)).ExitCode);
        await session.DisposeAsync();
        Assert.True(fixture.Broker.IsConnected(fixture.Agent));
    }

    [Fact]
    public async Task BoundedOutputClosesOnlySlowTerminal()
    {
        await using var fixture = await WireFixture.CreateAsync();
        var session = await fixture.OpenAsync();
        for (var i = 0; i < 17; i++) await fixture.SendAsync(new("terminal.output", fixture.LastSession, Data: "YQ=="));
        Assert.Equal("terminal.close", (await fixture.ReadAsync()).Type);
        await fixture.SendAsync(new("terminal.exited", fixture.LastSession, Data: "125"));
        Assert.True(fixture.Broker.IsConnected(fixture.Agent));
        for (var i = 0; i < 16; i++) Assert.Single((await session.ReadOutputAsync(fixture.Token)).Data.ToArray());
        await Assert.ThrowsAsync<IOException>(() => session.ReadOutputAsync(fixture.Token).AsTask());
    }

    [Fact]
    public async Task PerAgentSessionLimitIsBoundedAndReleasesOnClose()
    {
        await using var fixture = await WireFixture.CreateAsync();
        var sessions = new List<IAgentTerminalSession>();
        for (var i = 0; i < 16; i++) sessions.Add(await fixture.OpenAsync());
        await Assert.ThrowsAsync<IOException>(() => fixture.Broker.OpenTerminalAsync(fixture.Agent, Guid.NewGuid(), new("/bin/sh", [], Service: "app"), new(), fixture.Token));
        var closing = sessions[0].CloseAsync(fixture.Token).AsTask();
        var close = await fixture.ReadAsync(); Assert.Equal("terminal.close", close.Type);
        await fixture.SendAsync(new("terminal.exited", close.Id, Data: "125")); await closing;
        sessions.Add(await fixture.OpenAsync());
        fixture.Broker.Release(fixture.Agent, fixture.Session);
        foreach (var session in sessions.Skip(1)) await Assert.ThrowsAsync<IOException>(() => session.ReadOutputAsync(fixture.Token).AsTask());
    }

    [Fact]
    public async Task RevocationClosesAllTerminalQueuesAndClearsCapabilities()
    {
        await using var fixture = await WireFixture.CreateAsync();
        var session = await fixture.OpenAsync();
        fixture.Approved = false;
        await Assert.ThrowsAsync<IOException>(() => session.ReadOutputAsync(fixture.Token).AsTask());
        Assert.False(fixture.Broker.IsConnected(fixture.Agent)); Assert.Null(fixture.Broker.Capabilities(fixture.Agent));
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("")]
    public async Task MalformedTerminalOutputDisconnectsAgent(string data)
    {
        await using var fixture = await WireFixture.CreateAsync();
        var session = await fixture.OpenAsync();
        await fixture.SendAsync(new("terminal.output", fixture.LastSession, Data: data));
        await Assert.ThrowsAsync<IOException>(() => session.ReadOutputAsync(fixture.Token).AsTask());
        Assert.False(fixture.Broker.IsConnected(fixture.Agent));
    }

    [Fact]
    public async Task MalformedCapabilitiesAreRejectedWithoutPublishingSecrets()
    {
        await using var fixture = await WireFixture.CreateAsync();
        await fixture.SendAsync(new("capabilities", Guid.Empty, Data: "{\"runtime\":\"docker\",\"architecture\":\"x64\",\"password\":\"secret\"}"));
        await fixture.Running.WaitAsync(fixture.Token);
        Assert.Null(fixture.Broker.Capabilities(fixture.Agent));
    }

    private sealed class WireFixture : IAsyncDisposable
    {
        public AgentCommandBroker Broker { get; } = new();
        public Guid Agent { get; } = Guid.NewGuid();
        public AgentCommandSession Session { get; private set; } = null!;
        public Guid LastSession { get; private set; }
        public bool Approved { get; set; } = true;
        public Task Running { get; private set; } = null!;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        public CancellationToken Token => timeout.Token;
        private WebSocket client = null!;
        private WebSocket server = null!;
        public static async Task<WireFixture> CreateAsync()
        {
            var fixture = new WireFixture();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var connecting = new TcpClient();
            var connect = connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync(); await connect;
            fixture.client = WebSocket.CreateFromStream(connecting.GetStream(), false, null, Timeout.InfiniteTimeSpan);
            fixture.server = WebSocket.CreateFromStream(accepted.GetStream(), true, null, Timeout.InfiniteTimeSpan);
            fixture.Session = fixture.Broker.Reserve(fixture.Agent)!;
            fixture.Running = fixture.Session.RunAsync(fixture.server, _ => Task.FromResult(fixture.Approved), fixture.Token);
            Assert.Equal("hello", (await fixture.ReadAsync()).Type);
            await fixture.SendAsync(new("capabilities", Guid.Empty, Data: JsonSerializer.Serialize(new AgentCapabilities("docker", true, true, "29.0", "2.0", "x64", false, true), JsonSerializerOptions.Web)));
            while (fixture.Broker.Capabilities(fixture.Agent) is null) await Task.Delay(5, fixture.Token);
            return fixture;
        }
        public async Task<IAgentTerminalSession> OpenAsync()
        {
            var opening = Broker.OpenTerminalAsync(Agent, Guid.NewGuid(), new("/bin/sh", [], Service: "app"), new(), Token);
            var request = await ReadAsync(); Assert.Equal("terminal.open", request.Type); LastSession = request.Id;
            await SendAsync(new("terminal.opened", request.Id));
            return await opening;
        }
        public async Task SendAsync(AgentWireMessage message) => await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, Token);
        public async Task<AgentWireMessage> ReadAsync()
        {
            while (true)
            {
                var message = await AgentCommandChannel.ReceiveAsync(client, Token) ?? throw new IOException("Closed.");
                if (message.Type != "ping") return message;
                await SendAsync(new("pong", message.Id));
            }
        }
        public async ValueTask DisposeAsync()
        {
            timeout.Cancel(); client.Abort(); server.Abort();
            await Running;
            Broker.Release(Agent, Session); client.Dispose(); server.Dispose(); timeout.Dispose();
        }
    }
}
