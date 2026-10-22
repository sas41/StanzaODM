using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Terminals;
using Stanza.Core.Security;
using static Stanza.Tests.Terminals.TerminalTestServer;

namespace Stanza.Tests.Terminals;

public sealed class TerminalGatewayTests
{
    [Fact]
    public async Task RealHttpWebSocketStreamsBinaryInputOutputResizesExitAndMetadataOnlyAudit()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        var opened = Assert.Single(server.Transport.Opens);
        Assert.Equal(server.Agent, opened.Agent);
        Assert.Equal(server.Deployment, opened.Deployment);
        Assert.Equal("/bin/sh", opened.Execution.Executable);
        Assert.Equal("web", opened.Execution.Service);
        Assert.Equal((80, 24), await server.Transport.Session.Resizes.Reader.ReadAsync());
        var secret = Encoding.UTF8.GetBytes("password-secret\0\u001b[A");
        await SendAsync(socket, new { type = "input", data = Convert.ToBase64String(secret) });
        Assert.Equal(secret, await server.Transport.Session.Input.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await SendAsync(socket, new { type = "resize", columns = 120, rows = 42 });
        Assert.Equal((120, 42), await server.Transport.Session.Resizes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await server.Transport.Session.Output.Writer.WriteAsync(new(secret));
        var output = await ReceiveAsync(socket);
        Assert.Equal("output", output.GetProperty("type").GetString());
        Assert.Equal(secret, Convert.FromBase64String(output.GetProperty("data").GetString()!));
        await server.Transport.Session.Output.Writer.WriteAsync(new(ReadOnlyMemory<byte>.Empty, 7));
        var exit = await ReceiveAsync(socket);
        Assert.Equal("exit", exit.GetProperty("type").GetString());
        Assert.Equal(7, exit.GetProperty("exitCode").GetInt32());
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(server.Transport.Session.Closed);
        await WaitForAuditAsync(server);
        using var scope = server.App.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<Stanza.ControlPlane.Persistence.StanzaDbContext>().Set<AuditEvent>().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, entry => entry.Action == "terminal.opened" && entry.ActorId == server.Owner && entry.Resource.Contains(server.Deployment.ToString("N")) && entry.Resource.EndsWith(";s:web"));
        Assert.Contains(events, entry => entry.Action == "terminal.closed" && entry.Result.StartsWith("exited:7;session:"));
        Assert.Single(events.Select(entry => entry.Result.Split(";session:")[1]).Distinct());
        Assert.All(events, entry => Assert.DoesNotContain("secret", entry.Resource + entry.Result));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("https://evil.invalid")]
    [InlineData("http://user@localhost")]
    public async Task AnonymousAndForeignOriginCannotOpen(string origin)
    {
        await using var server = await CreateAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(null));
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(null, origin));
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie, origin));
        Assert.Empty(server.Transport.Opens);
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("stopped")]
    [InlineData("unknown")]
    [InlineData("missing-runtime")]
    [InlineData("agent-revoked")]
    public async Task UndeployedOrMismatchedOrUnapprovedTargetsAreDenied(string change)
    {
        await using var server = await CreateAsync();
        await server.MutateAsync(async db =>
        {
            var runtime = (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!;
            if (change == "unassigned") (await db.Set<DeploymentDraftEntity>().FindAsync(server.Deployment))!.TargetAgentId = Guid.NewGuid();
            if (change is "stopped" or "unknown") runtime.ObservedState = change;
            if (change == "missing-runtime") db.Remove(runtime);
            if (change == "agent-revoked") (await db.Set<AgentEntity>().FindAsync(server.Agent))!.Status = "revoked";
        });
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    [Theory]
    [InlineData("terminal.open")]
    [InlineData("deployment.read")]
    [InlineData("agent.read")]
    public async Task ExactTargetPermissionsAreAllRequired(string missing)
    {
        await using var server = await CreateAsync();
        Guid user;
        using (var scope = server.App.Services.CreateScope())
        {
            user = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(server.Owner, "reader", "a-secure-password-123", AccountRole.User)).Id;
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var action in new[] { "terminal.open", "deployment.read", "agent.read" })
                if (action != missing) await access.AddGrantAsync(server.Owner, user, null, action, action == "agent.read" ? "agent" : "deployment", action == "agent.read" ? server.Agent : server.Deployment);
            // A grant for another deployment cannot authorize this target.
            if (missing != "agent.read") await access.AddGrantAsync(server.Owner, user, null, missing, "deployment", Guid.NewGuid());
        }
        var cookie = await server.CookieAsync(user);
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stamp")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("reassigned")]
    [InlineData("redeployed")]
    [InlineData("permission")]
    [InlineData("agent-revoked")]
    public async Task ActiveSessionIsRevokedOnFifteenSecondTick(string change)
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        // Ready is sent immediately before monitor startup. Yield until its timer is registered.
        await Task.Delay(30);
        await server.MutateAsync(async db =>
        {
            var account = (await db.Accounts.FindAsync(server.Owner))!;
            if (change == "disabled") account.Enabled = false;
            if (change == "stamp") account.SecurityStamp = "different";
            if (change == "deleted") db.Accounts.Remove(account);
            if (change == "permission") account.Role = AccountRole.User;
            if (change == "reassigned") (await db.Set<DeploymentDraftEntity>().FindAsync(server.Deployment))!.TargetAgentId = null;
            if (change == "redeployed") (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!.Revision++;
            if (change == "agent-revoked") (await db.Set<AgentEntity>().FindAsync(server.Agent))!.Status = "revoked";
        });
        server.Clock.Advance(change == "expired" ? TimeSpan.FromHours(9) : TimeSpan.FromSeconds(15));
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(server.Transport.Session.Closed);
    }

    [Fact]
    public async Task InputRechecksRevocationBeforeForwardingWithoutWaitingForTimer()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        await server.MutateAsync(async db => (await db.Accounts.FindAsync(server.Owner))!.Enabled = false);
        await SendAsync(socket, new { type = "input", data = "c2VjcmV0" });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        Assert.False(server.Transport.Session.Input.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("invalid-json", 1008)]
    [InlineData("binary", 1008)]
    [InlineData("oversized", 1009)]
    [InlineData("invalid-resize", 1008)]
    [InlineData("oversized-input", 1008)]
    [InlineData("invalid-base64", 1008)]
    public async Task InvalidFramesCloseSessionAndCleanUp(string kind, int code)
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (kind == "invalid-json") await socket.SendAsync(Encoding.UTF8.GetBytes("not-json"), WebSocketMessageType.Text, true, timeout.Token);
        if (kind == "binary") await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, timeout.Token);
        if (kind == "oversized")
        {
            // Fragmented frames must not bypass the cumulative message bound.
            await socket.SendAsync(new byte[60000], WebSocketMessageType.Text, false, timeout.Token);
            await socket.SendAsync(new byte[60000], WebSocketMessageType.Text, true, timeout.Token);
        }
        if (kind == "invalid-resize") await SendAsync(socket, new { type = "resize", columns = 501, rows = 24 });
        if (kind == "oversized-input") await SendAsync(socket, new { type = "input", data = Convert.ToBase64String(new byte[65537]) });
        if (kind == "invalid-base64") await SendAsync(socket, new { type = "input", data = "invalid!" });
        await ExpectCloseAsync(socket, (WebSocketCloseStatus)code);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(server.Transport.Session.Closed);
    }

    [Fact]
    public async Task MaximumInputAndFragmentedValidMessagesAreForwardedExactly()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        var bytes = new byte[65536];
        Random.Shared.NextBytes(bytes);
        var json = Encoding.UTF8.GetBytes("{\"type\":\"input\",\"data\":\"" + Convert.ToBase64String(bytes) + "\"}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(json.AsMemory(0, 40000), WebSocketMessageType.Text, false, timeout.Token);
        await socket.SendAsync(json.AsMemory(40000), WebSocketMessageType.Text, true, timeout.Token);
        Assert.Equal(bytes, await server.Transport.Session.Input.Reader.ReadAsync(timeout.Token));
        await SendAsync(socket, new { type = "close" });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task DisconnectAndOfflineFailureDoNotLeakTransportDetails()
    {
        await using var server = await CreateAsync();
        using (var socket = await server.OpenAsync())
        {
            socket.Abort();
            await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(server.Transport.Session.Closed);
        }
        server.Transport.Unavailable = true;
        using var unavailable = await server.ConnectAsync(await server.CookieAsync());
        await SendAsync(unavailable, TerminalProtocolTests.Open);
        await ExpectCloseAsync(unavailable, WebSocketCloseStatus.InternalServerError);
    }

    [Fact]
    public async Task PerAccountConnectionLimitIsSharedWithLiveStatusAndReleasedAfterDisconnect()
    {
        await using var server = await CreateAsync();
        var cookie = await server.CookieAsync();
        var tracker = server.App.Services.GetRequiredService<Stanza.ControlPlane.Live.LiveConnectionLeases>();
        var leases = Enumerable.Range(0, 3).Select(_ => tracker.TryAcquire(server.Owner)!).ToArray();
        try
        {
            using var socket = await server.OpenAsync(cookie);
            await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
            await SendAsync(socket, new { type = "close" });
            await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
            await WaitForAuditAsync(server);
            using var lease = tracker.TryAcquire(server.Owner);
            Assert.NotNull(lease);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task NetworkOutputBackpressureTimesOutAndClosesTheRemoteSession()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        using var producerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var producer = Task.Run(async () =>
        {
            var bytes = new byte[65536];
            while (true) await server.Transport.Session.Output.Writer.WriteAsync(new(bytes), producerCancellation.Token);
        });
        try
        {
            // Deliberately never read the socket: the gateway must stop reading Agent output
            // when the network blocks and enforce its five-second send deadline.
            await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(server.Transport.Session.Closed);
        }
        finally
        {
            await producerCancellation.CancelAsync();
            try { await producer; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("both-targets")]
    [InlineData("no-target")]
    [InlineData("host-type")]
    public async Task InvalidOpeningHandshakeNeverCallsTheTransport(string kind)
    {
        await using var server = await CreateAsync();
        using var socket = await server.ConnectAsync(await server.CookieAsync());
        var message = TerminalProtocolTests.Open;
        if (kind == "both-targets") message = message with { Container = "other" };
        if (kind == "no-target") message = message with { Service = null };
        if (kind == "host-type") message = message with { Type = "host" };
        await SendAsync(socket, message);
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        Assert.Empty(server.Transport.Opens);
    }

    [Fact]
    public async Task OversizedAgentOutputFailsClosedWithoutSendingItsContents()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        await server.Transport.Session.Output.Writer.WriteAsync(new(new byte[65537]));
        await ExpectCloseAsync(socket, WebSocketCloseStatus.MessageTooBig);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConfiguredPublicOriginOverridesDirectOriginWithoutTrustingProxyHeaders()
    {
        await using var server = await CreateAsync();
        using (var scope = server.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InstanceSettingsService>().SaveEditorAsync(server.Owner, "https://control.example", null, 0);
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        using var socket = await server.ConnectAsync(cookie, "https://control.example");
        await SendAsync(socket, TerminalProtocolTests.Open);
        Assert.Equal("ready", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        await SendAsync(socket, new { type = "close" });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("disabled")]
    [InlineData("expired")]
    public async Task InitiallyStaleCookieCannotUpgradeOrOpenTransport(string change)
    {
        await using var server = await CreateAsync();
        var cookie = await server.CookieAsync();
        await server.MutateAsync(async db =>
        {
            var account = (await db.Accounts.FindAsync(server.Owner))!;
            if (change == "stamp") account.SecurityStamp = "new-stamp";
            if (change == "disabled") account.Enabled = false;
        });
        if (change == "expired") server.Clock.Advance(TimeSpan.FromHours(9));
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    private static async Task ExpectCloseAsync(WebSocket socket, WebSocketCloseStatus code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(new byte[256], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(code, result.CloseStatus);
        Assert.DoesNotContain("private", result.CloseStatusDescription);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }

    private static async Task WaitForAuditAsync(TerminalTestServer server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var scope = server.App.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<Stanza.ControlPlane.Persistence.StanzaDbContext>().Set<AuditEvent>().AnyAsync(x => x.Action == "terminal.closed", timeout.Token)) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}
