using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Stanza.Agent;
using Stanza.Agent.Enrollment;
using Stanza.Agent.Runtime;
using Stanza.Agent.Transport;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Controllers;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Agent;
using Stanza.Tests.Enrollment;

namespace Stanza.Tests.Transport;

public sealed class AgentTransportTests
{
    [Fact]
    public async Task Production_HTTP_agent_enrolls_waits_for_approval_syncs_executes_and_disconnects_on_revocation()
    {
        await using var fixture = await ServerFixture.CreateAsync("Production", allowInsecureHttp: true);
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path, ControlPlaneUrl = fixture.Origin,
            EnrollmentToken = fixture.Credential, AllowInsecureHttp = true });
        Assert.True(new AgentOptionsValidator(fixture.Environment).Validate(null, options.Value).Succeeded);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var enrollment = new EnrollmentClient(http, fixture.Store.Clock, options, fixture.Environment);
        AgentEnrollmentRequest Proof() => identity.CreateEnrollmentRequest(fixture.Credential, "http-agent", fixture.Store.Clock.GetUtcNow());
        Assert.Equal(EnrollmentStatus.Pending, await enrollment.RegisterAsync(new(fixture.Origin), Proof(), default));
        var pending = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal(AgentEnrollmentProtocol.Pending, pending.Status);
        Assert.False(fixture.Broker.IsConnected(pending.Id));
        using (var unapproved = fixture.Socket(identity))
            await Assert.ThrowsAsync<WebSocketException>(() => unapproved.ConnectAsync(fixture.ChannelUri, default));
        await fixture.Store.SetStatusAsync(pending.Id, AgentEnrollmentProtocol.Approved);
        Assert.Equal(EnrollmentStatus.Approved, await enrollment.RegisterAsync(new(fixture.Origin), Proof(), default));
        var executor = new RecordingExecutor(); var state = new AgentState();
        var channel = new AgentCommandChannel(options, state, executor, fixture.Store.Clock, environment: fixture.Environment);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = channel.RunAsync(identity, timeout.Token);
        await AgentTestWait.UntilAsync(() => state.Snapshot.Ready && fixture.Broker.ConfigurationSyncedAt(pending.Id) is not null);
        Assert.True(File.Exists(Path.Combine(root.Path, "configuration-cache", "agent.json")));
        var work = AgentWorkExecutorTests.Request();
        var result = await fixture.Broker.DispatchAsync(pending.Id, work, timeout.Token);
        Assert.True(result.Succeeded); Assert.Equal(work.Id, Assert.Single(executor.Requests).Id);
        await fixture.Store.SetStatusAsync(pending.Id, AgentEnrollmentProtocol.Revoked);
        // The server deliberately checks revocation every five seconds. Wait for channel exit
        // with a deadline longer than that interval, but shorter than the test's cancellation.
        await IgnoreConnectionCloseAsync(running.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.False(fixture.Broker.IsConnected(pending.Id));
        Assert.False(state.Snapshot.CanExecuteWork);
    }

    [Fact]
    public async Task Production_HTTP_enrollment_is_refused_before_registration_when_CP_opt_in_is_off()
    {
        await using var fixture = await ServerFixture.CreateAsync("Production");
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        using var http = new HttpClient();
        var request = identity.CreateEnrollmentRequest(fixture.Credential, "blocked-http-agent", fixture.Store.Clock.GetUtcNow());
        using var response = await http.PostAsJsonAsync(new Uri(new Uri(fixture.Origin), AgentEnrollmentProtocol.Endpoint), request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(fixture.Credential, await response.Content.ReadAsStringAsync());
        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Agent_refuses_cleartext_without_its_own_opt_in_even_when_CP_allows_it()
    {
        await using var fixture = await ServerFixture.CreateAsync("Production", allowInsecureHttp: true);
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path, ControlPlaneUrl = fixture.Origin,
            EnrollmentToken = fixture.Credential });
        Assert.True(new AgentOptionsValidator(fixture.Environment).Validate(null, options.Value).Failed);
        using var http = new HttpClient();
        var enrollment = new EnrollmentClient(http, fixture.Store.Clock, options, fixture.Environment);
        Assert.Equal(EnrollmentStatus.Unavailable, await enrollment.RegisterAsync(new(fixture.Origin),
            identity.CreateEnrollmentRequest(fixture.Credential, "blocked-agent", fixture.Store.Clock.GetUtcNow()), default));
        await Assert.ThrowsAsync<IOException>(() => new AgentCommandChannel(options, new AgentState(), new RecordingExecutor(), fixture.Store.Clock)
            .RunAsync(identity, default));
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Empty(fixture.Broker.ConnectedAgentIds);
    }

    [Fact]
    public async Task Authenticated_channel_syncs_at_boot_on_change_and_in_full_after_reconnect_without_executing_work()
    {
        await using var fixture = await ServerFixture.CreateAsync();
        using var root = new AgentTestDirectory(); using var identity = AgentIdentity.LoadOrCreate(root.Path);
        var agent = await fixture.RegisterAsync(identity, true);
        Guid deployment;
        await using (var db = fixture.Store.CreateContext())
        {
            var access = new AccessService(db, fixture.Store.Clock);
            await new OperationalPolicyService(db, access).SaveAsync(fixture.Store.OwnerId, new(new() { StatusIntervalSeconds = 1 }, 0));
            var drafts = new DeploymentDraftService(db, fixture.Store.Protection, new AccountService(db, fixture.Store.Clock), access, fixture.Store.Clock);
            deployment = (await drafts.CreateAsync(fixture.Store.OwnerId, "Synced app", agent, "services: {}\n", "LOCAL=initial\n")).Id;
        }
        var options = Options.Create(new AgentOptions { PersistentDataRoot = root.Path, ControlPlaneUrl = fixture.Origin,
            EnrollmentToken = fixture.Credential, AllowInsecureHttp = true });
        var executor = new RecordingExecutor(); var state = new AgentState();
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = new AgentCommandChannel(options, state, executor, fixture.Store.Clock).RunAsync(identity, first.Token);
        await AgentTestWait.UntilAsync(() => state.Snapshot.Ready && fixture.Broker.ConfigurationSyncedAt(agent) is not null);
        var cache = Path.Combine(root.Path, "configuration-cache");
        var compose = Path.Combine(cache, "deployments", deployment.ToString("N"), "files", "compose.yaml");
        Assert.Equal("services: {}\n", await File.ReadAllTextAsync(compose));
        await using (var db = fixture.Store.CreateContext())
        {
            await new InstanceSettingsService(db, fixture.Store.Protection).SaveAsync(fixture.Store.OwnerId, new("", "UPDATED=global\n", 0));
            await fixture.Store.Service(db).SaveEnvironmentAsync(fixture.Store.OwnerId, agent, new("UPDATED=agent\n", 1));
            var access = new AccessService(db, fixture.Store.Clock);
            await new AgentPolicyService(db, access, new OperationalPolicyService(db, access)).SaveAsync(fixture.Store.OwnerId, new(agent, new(false, 99), 0));
            await new DeploymentDraftService(db, fixture.Store.Protection, new AccountService(db, fixture.Store.Clock), access, fixture.Store.Clock)
                .SaveAsync(fixture.Store.OwnerId, deployment, new("Synced app", agent, 1, "# changed\nservices: {}\n", "LOCAL=changed\n"));
        }
        await AgentTestWait.UntilAsync(() => File.Exists(compose) && File.ReadAllText(compose).StartsWith("# changed", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(cache, "agent.env")) == "UPDATED=agent\n");
        await AgentTestWait.UntilAsync(() => !fixture.Broker.ConfigurationPending(agent));
        var saved = JsonSerializer.Deserialize<AgentConfiguration>(await File.ReadAllTextAsync(Path.Combine(cache, "agent.json")), JsonSerializerOptions.Web)!;
        Assert.Equal("UPDATED=global\n", saved.GlobalEnvironment); Assert.False(saved.Policy.ExecutionEnabled); Assert.Equal(99, saved.Policy.TimeoutSeconds);
        first.Cancel(); await IgnoreConnectionCloseAsync(running);
        await AgentTestWait.UntilAsync(() => !fixture.Broker.IsConnected(agent));
        await File.WriteAllTextAsync(compose, "local corruption");
        var reconnected = new AgentState(); using var second = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        running = new AgentCommandChannel(options, reconnected, executor, fixture.Store.Clock).RunAsync(identity, second.Token);
        await AgentTestWait.UntilAsync(() => reconnected.Snapshot.Ready && fixture.Broker.ConfigurationSyncedAt(agent) is not null);
        Assert.Equal("# changed\nservices: {}\n", await File.ReadAllTextAsync(compose));
        Assert.Empty(executor.Requests);
        second.Cancel(); await IgnoreConnectionCloseAsync(running);
    }

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
            ControlPlaneUrl = fixture.Origin, EnrollmentToken = fixture.Credential, AllowInsecureHttp = true }), state, executor, fixture.Store.Clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = channel.RunAsync(identity, cancellation.Token);
        await AgentTestWait.UntilAsync(() => state.Snapshot.Ready && fixture.Broker.ConfigurationSyncedAt(agentId) is not null);
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
        public IHostEnvironment Environment => app.Environment;
        public Uri ChannelUri => new(new UriBuilder(Origin) { Scheme = "ws", Path = "/api/agent-channel" }.ToString());
        public AgentCommandBroker Broker => app.Services.GetRequiredService<AgentCommandBroker>();
        private ServerFixture(WebApplication application, EnrollmentTestStore store, string credential)
        { app = application; Store = store; Credential = credential; }
        public static async Task<ServerFixture> CreateAsync(string environment = "Development", bool allowInsecureHttp = false)
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
            builder.Services.AddSingleton(new HttpTransportPolicy(allowInsecureHttp));
            AgentTransportExtensions.AddStanzaAgentTransport(builder.Services);
            builder.Services.AddControllers().AddApplicationPart(typeof(AgentEnrollmentController).Assembly);
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("enrollment", limiter =>
            { limiter.PermitLimit = 100; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; }));
            var app = builder.Build(); app.UseRouting(); app.UseRateLimiter(); app.UseWebSockets();
            app.MapControllers(); AgentTransportExtensions.MapStanzaAgentTransport(app);
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
