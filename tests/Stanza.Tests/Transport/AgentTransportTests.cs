using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;
using Stanza.Tests.Enrollment;

namespace Stanza.Tests.Transport;

public sealed class AgentTransportTests
{
    [Fact]
    public void BrokerReservesOneSessionPerPersistentIdentityAndReleasesOnlyItsOwner()
    {
        var broker = new AgentCommandBroker(); var id = Guid.NewGuid();
        var first = broker.Reserve(id)!;
        Assert.NotNull(first); Assert.Null(broker.Reserve(id)); Assert.False(broker.IsConnected(id));
        broker.Release(id, new AgentCommandSession()); Assert.Null(broker.Reserve(id));
        broker.Release(id, first); var second = broker.Reserve(id)!; Assert.NotNull(second); broker.Release(id, second);
    }

    [Fact]
    public async Task OfflineDispatchFailsWithoutPretendingToExecute()
    {
        var broker = new AgentCommandBroker();
        await Assert.ThrowsAsync<AgentUnavailableException>(() => broker.DispatchAsync(Guid.NewGuid(), AgentWorkExecutorTests.Request()));
    }

    [Fact]
    public async Task RealOutboundAgentChannelAuthenticatesCorrelatesWorkAndCleansUp()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agentId = await fixture.RegisterAsync(identity, approve: true);
        var state = new AgentState(); state.Set(new(false, EnrollmentStatus.Approved, identity.AgentId));
        var executor = new RecordingExecutor();
        var channel = new AgentCommandChannel(Options.Create(new AgentOptions { PersistentDataRoot = root.Path,
            ControlPlaneUrl = fixture.Origin, EnrollmentToken = fixture.Credential, AllowLoopbackHttp = true }), state, executor, fixture.Store.Clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = channel.RunAsync(identity, cancellation.Token);
        await AgentTestWait.UntilAsync(() => state.Snapshot.CommandChannelAvailable && fixture.Broker.IsConnected(agentId));
        Assert.True(state.Snapshot.Ready); Assert.True(state.Snapshot.CanExecuteWork);
        var request = AgentWorkExecutorTests.Request();
        var result = await fixture.Broker.DispatchAsync(agentId, request, cancellation.Token);
        Assert.True(result.Succeeded); Assert.Equal(request.Id, result.Id); Assert.Equal("sensitive-output", result.Output);
        Assert.Equal(request.Id, Assert.Single(executor.Requests).Id);
        cancellation.Cancel();
        await IgnoreConnectionCloseAsync(running);
        await AgentTestWait.UntilAsync(() => !fixture.Broker.IsConnected(agentId));
        Assert.False(state.Snapshot.CommandChannelAvailable); Assert.False(state.Snapshot.CanExecuteWork); Assert.False(state.Snapshot.Ready);
    }

    [Fact]
    public async Task ApprovalReplayAndDuplicateLiveSessionAreEnforcedAtActualUpgrade()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agentId = await fixture.RegisterAsync(identity, approve: false);
        using (var pending = fixture.Socket(identity)) await Assert.ThrowsAsync<WebSocketException>(() => pending.ConnectAsync(fixture.ChannelUri, default));
        await fixture.Store.SetStatusAsync(agentId, AgentEnrollmentProtocol.Approved);
        var proof = identity.CreateEnrollmentRequest(fixture.Credential, "test", fixture.Store.Clock.GetUtcNow());
        using var first = fixture.Socket(proof); await first.ConnectAsync(fixture.ChannelUri, default);
        Assert.Equal("hello", (await AgentCommandChannel.ReceiveAsync(first, default))!.Type);
        using (var duplicate = fixture.Socket(identity)) await Assert.ThrowsAsync<WebSocketException>(() => duplicate.ConnectAsync(fixture.ChannelUri, default));
        first.Abort();
        await AgentTestWait.UntilAsync(() => !fixture.Broker.IsConnected(agentId));
        using var replay = fixture.Socket(proof);
        await Assert.ThrowsAsync<WebSocketException>(() => replay.ConnectAsync(fixture.ChannelUri, default));
    }

    [Fact]
    public async Task RevocationClosesActiveConnectionAndOutstandingWorkBecomesUncertain()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agentId = await fixture.RegisterAsync(identity, true);
        using var socket = fixture.Socket(identity); await socket.ConnectAsync(fixture.ChannelUri, default);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await AgentTestWait.UntilAsync(() => fixture.Broker.IsConnected(agentId));
        var request = AgentWorkExecutorTests.Request("deploy");
        var dispatched = fixture.Broker.DispatchAsync(agentId, request, timeout.Token);
        var received = await AgentCommandChannel.ReceiveAsync(socket, timeout.Token);
        Assert.Equal(request.Id, received!.Id);
        await fixture.Store.SetStatusAsync(agentId, AgentEnrollmentProtocol.Revoked);
        Assert.Equal("uncertain", (await dispatched.WaitAsync(timeout.Token)).Code);
        Assert.False(fixture.Broker.IsConnected(agentId));
    }

    [Fact]
    public async Task CancellationAfterDispatchReturnsUncertainAndDisconnectsRatherThanRetrying()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agentId = await fixture.RegisterAsync(identity, true);
        using var socket = fixture.Socket(identity); await socket.ConnectAsync(fixture.ChannelUri, default);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        using var cancellation = new CancellationTokenSource();
        await AgentTestWait.UntilAsync(() => fixture.Broker.IsConnected(agentId));
        var dispatched = fixture.Broker.DispatchAsync(agentId, AgentWorkExecutorTests.Request("deploy"), cancellation.Token);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        cancellation.Cancel();
        Assert.Equal("uncertain", (await dispatched).Code);
        Assert.False(fixture.Broker.IsConnected(agentId));
    }

    [Fact]
    public async Task MissingProofAndNonDevelopmentPlainHttpAreRejected()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var missing = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => missing.ConnectAsync(fixture.ChannelUri, default));
        await using var production = await ServerFixture.CreateAsync("Production");
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        await production.RegisterAsync(identity, true);
        using var socket = production.Socket(identity);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(production.ChannelUri, default));
    }

    [Fact]
    public async Task UnknownTerminalMessagesAreRejectedRatherThanPretendingToProvideATerminal()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var id = await fixture.RegisterAsync(identity, true);
        using var socket = fixture.Socket(identity); await socket.ConnectAsync(fixture.ChannelUri, default);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new AgentWireMessage("terminal.open", Guid.NewGuid()), JsonSerializerOptions.Web),
            WebSocketMessageType.Text, true, default);
        await AgentTestWait.UntilAsync(() => !fixture.Broker.IsConnected(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryAndOversizedFragmentedFramesCloseTheConnection(bool oversized)
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var id = await fixture.RegisterAsync(identity, true);
        using var socket = fixture.Socket(identity); await socket.ConnectAsync(fixture.ChannelUri, default);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        try
        {
            if (oversized)
            {
                var buffer = new byte[16384]; Array.Fill(buffer, (byte)' ');
                for (var i = 0; i <= 2048; i++)
                    await socket.SendAsync(buffer, WebSocketMessageType.Text, i == 2048, default);
            }
            else await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, default);
        }
        catch (WebSocketException) { }
        await AgentTestWait.UntilAsync(() => !fixture.Broker.IsConnected(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedResultCodesAndPolicyOutputOverrunsAreUncertain(bool oversizedOutput)
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agentId = await fixture.RegisterAsync(identity, true);
        using var socket = fixture.Socket(identity); await socket.ConnectAsync(fixture.ChannelUri, default);
        await AgentCommandChannel.ReceiveAsync(socket, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await AgentTestWait.UntilAsync(() => fixture.Broker.IsConnected(agentId));
        var request = AgentWorkExecutorTests.Request() with { Policy = new(MaximumOutputCharacters: 8) };
        var dispatched = fixture.Broker.DispatchAsync(agentId, request, timeout.Token);
        await AgentCommandChannel.ReceiveAsync(socket, timeout.Token);
        var response = new AgentWorkResponse(request.Id, true, oversizedOutput ? "succeeded" : "invalid result code",
            oversizedOutput ? new string('x', 64) : null);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new AgentWireMessage("work.response", request.Id, Response: response), JsonSerializerOptions.Web),
            WebSocketMessageType.Text, true, timeout.Token);
        Assert.Equal("uncertain", (await dispatched.WaitAsync(timeout.Token)).Code);
        Assert.False(fixture.Broker.IsConnected(agentId));
    }

    private static async Task IgnoreConnectionCloseAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    }

    private sealed class RecordingExecutor : IAgentWorkExecutor
    {
        public List<AgentWorkRequest> Requests { get; } = [];
        public Task<AgentWorkResponse> ExecuteAsync(AgentWorkRequest request, CancellationToken token = default)
        { Requests.Add(request); return Task.FromResult(new AgentWorkResponse(request.Id, true, "succeeded", "sensitive-output")); }
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly WebApplication app;
        public EnrollmentTestStore Store { get; }
        public string Credential { get; }
        public string Origin => app.Urls.Single();
        public Uri ChannelUri => new(new UriBuilder(Origin) { Scheme = "ws", Path = "/api/agent-channel" }.ToString());
        public AgentCommandBroker Broker => app.Services.GetRequiredService<AgentCommandBroker>();
        private ServerFixture(WebApplication application, EnrollmentTestStore store, string credential)
        { app = application; Store = store; Credential = credential; }
        public static async Task<ServerFixture> CreateAsync(string environment = "Development")
        {
            var store = await EnrollmentTestStore.CreateAsync();
            var credential = (await store.IssueAsync()).Token;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(store.Clock);
            builder.Services.AddSingleton(store.Protection);
            builder.Services.AddScoped(_ => store.CreateContext());
            builder.Services.AddScoped<AgentAdministrationService>();
            AgentTransportExtensions.AddStanzaAgentTransport(builder.Services);
            var app = builder.Build(); app.UseWebSockets(); AgentTransportExtensions.MapStanzaAgentTransport(app);
            await app.StartAsync();
            return new(app, store, credential);
        }
        public async Task<Guid> RegisterAsync(AgentIdentity identity, bool approve)
        {
            await Store.RegisterAsync(identity.CreateEnrollmentRequest(Credential, "test", Store.Clock.GetUtcNow()));
            var id = (await Store.ListAsync()).Single().Id;
            if (approve) await Store.SetStatusAsync(id, AgentEnrollmentProtocol.Approved);
            return id;
        }
        public ClientWebSocket Socket(AgentIdentity identity) => Socket(identity.CreateEnrollmentRequest(Credential, "test", Store.Clock.GetUtcNow()));
        public ClientWebSocket Socket(AgentEnrollmentRequest proof)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Stanza-Proof", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(proof, JsonSerializerOptions.Web)));
            return socket;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); await Store.DisposeAsync(); }
    }
}
