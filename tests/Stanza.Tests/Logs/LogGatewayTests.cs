using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Logs;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using static Stanza.Tests.Logs.LogTestServer;

namespace Stanza.Tests.Logs;

public sealed class LogGatewayTests
{
    [Theory]
    [InlineData("running", "service")]
    [InlineData("stopped", "container")]
    [InlineData("unknown", "service")]
    public async Task ActualWebSocketStreamsContinuousReadOnlyLogsForRecordedDeploymentWithoutOperations(string state, string kind)
    {
        await using var server = await CreateAsync();
        await server.MutateAsync(async db => (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!.ObservedState = state);
        var selector = kind == "service" ? new LogMessage("open", Service: "web") : new("open", Container: "app-container");
        using var socket = await server.OpenAsync(message: selector);
        var opened = Assert.Single(server.Transport.Opens);
        Assert.Equal(server.Agent, opened.Agent);
        Assert.Equal(server.Deployment, opened.Deployment);
        Assert.Equal("logs", opened.Execution.Mode);
        Assert.Equal(selector.Service, opened.Execution.Service);
        Assert.Equal(selector.Container, opened.Execution.Container);
        Assert.Empty(opened.Execution.Arguments);
        var secret = Encoding.UTF8.GetBytes("2026-10-22T12:00:00Z password-secret <script>alert(1)</script>\n");
        // Separate output events show this is a continuous stream, not queued snapshot operations.
        for (var i = 0; i < 3; i++)
        {
            await server.Transport.Session.Output.Writer.WriteAsync(new(secret));
            var message = await ReceiveAsync(socket);
            Assert.Equal("output", message.GetProperty("type").GetString());
            Assert.Equal(secret, Convert.FromBase64String(message.GetProperty("data").GetString()!));
        }
        await server.Transport.Session.Output.Writer.WriteAsync(new(ReadOnlyMemory<byte>.Empty, 0));
        var ended = await ReceiveAsync(socket);
        Assert.Equal("end", ended.GetProperty("type").GetString());
        Assert.Equal(0, ended.GetProperty("code").GetInt32());
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.WaitForAuditAsync();
        Assert.True(server.Transport.Session.Closed);
        Assert.Equal(0, server.Transport.Session.InputCalls);
        Assert.Equal(0, server.Transport.Session.ResizeCalls);
        using var scope = server.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
        Assert.Empty(await db.Set<OperationEntity>().ToListAsync());
        var entries = await db.Set<AuditEvent>().ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Action == "log.open" && entry.ActorId == server.Owner && entry.Resource.Contains(server.Deployment.ToString("N")));
        Assert.Contains(entries, entry => entry.Action == "log.closed" && entry.Result.StartsWith("ended:0;session:"));
        Assert.Single(entries.Select(entry => entry.Result.Split(";session:")[1]).Distinct());
        Assert.All(entries, entry => Assert.DoesNotContain("password-secret", entry.Resource + entry.Result + entry.Action));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("https://foreign.invalid")]
    [InlineData("http://user@localhost")]
    public async Task AnonymousOrMissingMalformedForeignOriginCannotUpgrade(string origin)
    {
        await using var server = await CreateAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(null));
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie, origin));
        Assert.Empty(server.Transport.Opens);
    }

    [Fact]
    public async Task ConfiguredPublicOriginIsUsedWithoutTrustingForwardedHeaders()
    {
        await using var server = await CreateAsync();
        using (var scope = server.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InstanceSettingsService>().SaveEditorAsync(server.Owner, "https://control.example", null, 0);
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        using var socket = await server.ConnectAsync(cookie, "https://control.example");
        await SendAsync(socket, new LogMessage("open", Service: "web"));
        await ReceiveAsync(socket);
        await SendAsync(socket, new { type = "close" });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("missing-runtime")]
    [InlineData("not-deployed")]
    [InlineData("agent-revoked")]
    [InlineData("agent-missing")]
    public async Task OnlyRecordedCurrentlyAssignedApprovedAgentCanReceiveLogRequests(string change)
    {
        await using var server = await CreateAsync();
        await server.MutateAsync(async db =>
        {
            var runtime = (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!;
            if (change == "unassigned") (await db.Set<DeploymentDraftEntity>().FindAsync(server.Deployment))!.TargetAgentId = Guid.NewGuid();
            if (change == "missing-runtime") db.Remove(runtime);
            if (change == "not-deployed") runtime.Revision = 0;
            var agent = (await db.Set<AgentEntity>().FindAsync(server.Agent))!;
            if (change == "agent-revoked") agent.Status = "revoked";
            if (change == "agent-missing") db.Remove(agent);
        });
        var cookie = await server.CookieAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    [Theory]
    [InlineData("logs.read")]
    [InlineData("deployment.read")]
    [InlineData("agent.read")]
    public async Task EachExactTargetPermissionIsRequiredAndWrongResourceGrantsDoNotAuthorize(string missing)
    {
        await using var server = await CreateAsync();
        Guid user;
        using (var scope = server.App.Services.CreateScope())
        {
            user = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(server.Owner, "reader", "a-secure-password-123", AccountRole.User)).Id;
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var action in new[] { "logs.read", "deployment.read", "agent.read" })
                await access.AddGrantAsync(server.Owner, user, null, action, action == "agent.read" ? "agent" : "deployment",
                    action == missing ? Guid.NewGuid() : action == "agent.read" ? server.Agent : server.Deployment);
        }
        var cookie = await server.CookieAsync(user);
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    [Fact]
    public async Task LogReaderNeedsNoTerminalPermissionAndDirectGrantRevocationClosesWithoutStampChange()
    {
        await using var server = await CreateAsync();
        Guid user;
        using (var scope = server.App.Services.CreateScope())
        {
            user = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(server.Owner, "reader", "a-secure-password-123", AccountRole.User)).Id;
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var action in new[] { "logs.read", "deployment.read", "agent.read" })
                await access.AddGrantAsync(server.Owner, user, null, action, action == "agent.read" ? "agent" : "deployment", action == "agent.read" ? server.Agent : server.Deployment);
        }
        using var socket = await server.OpenAsync(await server.CookieAsync(user));
        await Task.Delay(30);
        await server.MutateAsync(async db => db.Grants.Remove(await db.Grants.SingleAsync(x => x.AccountId == user && x.Action == "logs.read")));
        server.Clock.Advance(TimeSpan.FromSeconds(15));
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stamp")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("reassigned")]
    [InlineData("redeployed")]
    [InlineData("agent-revoked")]
    public async Task IdentityOrTargetRevocationClosesActiveStreamOnFifteenSecondTick(string change)
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        await Task.Delay(30);
        await server.MutateAsync(async db =>
        {
            var account = (await db.Accounts.FindAsync(server.Owner))!;
            if (change == "disabled") account.Enabled = false;
            if (change == "stamp") account.SecurityStamp = "different";
            if (change == "deleted") db.Remove(account);
            if (change == "reassigned") (await db.Set<DeploymentDraftEntity>().FindAsync(server.Deployment))!.TargetAgentId = null;
            if (change == "redeployed") (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!.Revision++;
            if (change == "agent-revoked") (await db.Set<AgentEntity>().FindAsync(server.Agent))!.Status = "revoked";
        });
        server.Clock.Advance(change == "expired" ? TimeSpan.FromHours(9) : TimeSpan.FromSeconds(15));
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(server.Transport.Session.Closed);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("resize")]
    [InlineData("open")]
    [InlineData("binary")]
    [InlineData("malformed")]
    public async Task StreamRejectsAllApplicationInputExceptCloseWithoutCallingInputOrResize(string kind)
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (kind == "binary") await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, timeout.Token);
        else if (kind == "malformed") await socket.SendAsync(Encoding.UTF8.GetBytes("invalid"), WebSocketMessageType.Text, true, timeout.Token);
        else await SendAsync(socket, new { type = kind });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        await server.Transport.Session.Disposed.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, server.Transport.Session.InputCalls);
        Assert.Equal(0, server.Transport.Session.ResizeCalls);
    }

    [Fact]
    public async Task CumulativeFragmentedClientMessageLimitIsEnforced()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(new byte[1500], WebSocketMessageType.Text, false, timeout.Token);
        await socket.SendAsync(new byte[1500], WebSocketMessageType.Text, true, timeout.Token);
        await ExpectCloseAsync(socket, WebSocketCloseStatus.MessageTooBig);
        await server.Transport.Session.Disposed.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task OversizedAgentOutputFailsClosedBeforeSendingContents()
    {
        await using var server = await CreateAsync();
        using var socket = await server.OpenAsync();
        await server.Transport.Session.Output.Writer.WriteAsync(new(new byte[65537]));
        await ExpectCloseAsync(socket, WebSocketCloseStatus.MessageTooBig);
        await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OfflineFailureAndUnexpectedDisconnectCleanUpWithoutLeakingDetails()
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
        await SendAsync(unavailable, new LogMessage("open", Service: "web"));
        await ExpectCloseAsync(unavailable, WebSocketCloseStatus.InternalServerError);
    }

    [Fact]
    public async Task SharedOperationalConnectionPolicyLimitsAndReleasesLogLeases()
    {
        await using var server = await CreateAsync();
        using (var scope = server.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OperationalPolicyService>().SaveAsync(server.Owner, new(new() { MaximumConnectionsPerUser = 1 }, 0));
        var cookie = await server.CookieAsync();
        var leases = server.App.Services.GetRequiredService<LiveConnectionLeases>();
        using var socket = await server.OpenAsync(cookie);
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Null(leases.TryAcquire(server.Owner, 1, 1024));
        await SendAsync(socket, new { type = "close" });
        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
        await server.WaitForAuditAsync();
        using var lease = leases.TryAcquire(server.Owner, 1, 1024);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task NetworkBackpressureDeadlineClosesAgentLogSessionInsteadOfBufferingForever()
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
            // Never read the client socket after ready; real TCP backpressure must stop the Agent reader.
            await server.Transport.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(server.Transport.Session.Closed);
        }
        finally
        {
            await producerCancellation.CancelAsync();
            try { await producer; } catch (OperationCanceledException) { }
        }
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
}
