using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane;
using Stanza.ControlPlane.Live;
using Stanza.Core.Security;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;
using Stanza.Tests.Web;

namespace Stanza.Tests.Live;

public sealed class LiveStatusTests
{
    [Fact]
    public async Task AnonymousRequestsCannotOpenTheFeed()
    {
        await using var root = new StanzaFactory();
        await using var factory = WithLiveStatus(root);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/live/status");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.ToString());
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri("ws://localhost/live/status"), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://evil.invalid")]
    [InlineData("https://localhost")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://localhost/path")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost?query=1")]
    [InlineData("http://localhost#fragment")]
    [InlineData("http://owner@localhost")]
    [InlineData("http://localhost, http://evil.invalid")]
    public async Task MissingMalformedOrForeignOriginIsDenied(string? origin)
    {
        await using var root = new StanzaFactory();
        await using var factory = WithLiveStatus(root);
        await BootstrapAsync(factory);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client);
        var response = await RequestFeedAsync(client, origin);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(factory, cookie, origin));
    }

    [Fact]
    public async Task ForwardedHeadersCannotAuthorizeForeignOrigin()
    {
        await using var root = new StanzaFactory();
        await using var factory = WithLiveStatus(root);
        await BootstrapAsync(factory);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/live/status");
        request.Headers.Add("Origin", "https://evil.invalid");
        request.Headers.Add("X-Forwarded-Host", "evil.invalid");
        request.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(AccountRole.SystemAdmin)]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.User)]
    public async Task InitialStatusUsesCurrentRoleAndOnlyAdministratorsGetCounts(AccountRole role)
    {
        await using var root = new StanzaFactory();
        await using var factory = WithLiveStatus(root);
        var owner = await BootstrapAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            if (role != AccountRole.SystemAdmin)
                await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(owner, "reader", "a-secure-password-123", role);
            var database = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            database.Set<AgentEntity>().AddRange(
                new AgentEntity { Id = Guid.NewGuid(), Identity = "pending-agent", Hostname = "pending-host", PublicKey = "key1", Status = "pending" },
                new AgentEntity { Id = Guid.NewGuid(), Identity = "approved-agent", Hostname = "approved-host", PublicKey = "key2", Status = "approved" });
            await database.SaveChangesAsync();
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client, role == AccountRole.SystemAdmin ? "owner" : "reader");
        using var socket = await ConnectAsync(factory, cookie);
        var status = await ReceiveStatusAsync(socket);
        Assert.True(status.GetProperty("databaseReady").GetBoolean());
        if (role == AccountRole.User)
        {
            Assert.Equal(JsonValueKind.Null, status.GetProperty("agentCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingAgentCount").ValueKind);
        }
        else
        {
            Assert.Equal(2, status.GetProperty("agentCount").GetInt32());
            Assert.Equal(1, status.GetProperty("pendingAgentCount").GetInt32());
        }
        await CloseAsync(socket);
    }

    [Theory]
    [InlineData(WebSocketMessageType.Text)]
    [InlineData(WebSocketMessageType.Binary)]
    public async Task ApplicationMessagesAreRejectedAndLeaseIsReleased(WebSocketMessageType type)
    {
        await using var root = new StanzaFactory();
        await using var factory = WithLiveStatus(root);
        await BootstrapAsync(factory);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client);
        using var socket = await ConnectAsync(factory, cookie);
        await ReceiveStatusAsync(socket);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(new byte[] { 42 }, type, true, timeout.Token);
        var result = await socket.ReceiveAsync(new byte[256], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        using var replacement = await ConnectAsync(factory, cookie);
        await ReceiveStatusAsync(replacement);
        await CloseAsync(replacement);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stamp")]
    [InlineData("expired")]
    [InlineData("deleted")]
    public async Task ActiveSessionsAreClosedOnTheNextControlledTick(string change)
    {
        var clock = new LiveTestClock();
        await using var root = new StanzaFactory(clock);
        await using var factory = WithLiveStatus(root, clock);
        Guid owner;
        Guid user;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            owner = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            user = (await accounts.CreateAsync(owner, "reader", "a-secure-password-123", AccountRole.User)).Id;
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client, "reader");
        using var socket = await ConnectAsync(factory, cookie);
        await ReceiveStatusAsync(socket);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            var account = await db.Accounts.FindAsync(user);
            Assert.NotNull(account);
            if (change == "disabled") account.Enabled = false;
            if (change == "stamp") account.SecurityStamp = Guid.NewGuid().ToString("N");
            if (change == "deleted") db.Accounts.Remove(account);
            await db.SaveChangesAsync();
        }
        clock.Advance(change == "expired" ? TimeSpan.FromHours(9) : TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(new byte[256], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }

    [Fact]
    public async Task SendBackpressureTimesOutAndReleasesTheConnectionLease()
    {
        var clock = new LiveTestClock();
        var socket = new BlockedStatusSocket();
        await using var root = new StanzaFactory(clock);
        await using var live = WithLiveStatus(root, clock);
        await using var factory = live.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(socket);
            services.AddTransient<IStartupFilter, BlockedSocketHosting>();
        }));
        var owner = await BootstrapAsync(factory);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginAsync(client);
        var response = RequestFeedAsync(client, "http://localhost");
        await socket.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(socket.PayloadSize, 1, 256);
        clock.Advance(TimeSpan.FromSeconds(5));
        await response.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(socket.SendWasCancelled);
        Assert.Equal(WebSocketState.Closed, socket.State);
        var tracker = factory.Services.GetRequiredService<LiveConnectionLeases>();
        var leases = Enumerable.Range(0, 4).Select(_ => tracker.TryAcquire(owner)).ToArray();
        try { Assert.All(leases, lease => Assert.NotNull(lease)); }
        finally { foreach (var lease in leases) lease?.Dispose(); }
    }

    [Fact]
    public async Task RoleIsReloadedRatherThanTakenFromTheOriginalCookie()
    {
        var clock = new LiveTestClock();
        await using var root = new StanzaFactory(clock);
        await using var factory = WithLiveStatus(root, clock);
        var owner = await BootstrapAsync(factory);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client);
        using var socket = await ConnectAsync(factory, cookie);
        Assert.Equal(0, (await ReceiveStatusAsync(socket)).GetProperty("agentCount").GetInt32());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            var account = await db.Accounts.FindAsync(owner);
            Assert.NotNull(account);
            // Deliberately leave the stamp unchanged to prove the feed never trusts stale role claims.
            account.Role = AccountRole.User;
            await db.SaveChangesAsync();
        }
        clock.Advance(TimeSpan.FromSeconds(15));
        var status = await ReceiveStatusAsync(socket);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("agentCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingAgentCount").ValueKind);
        await CloseAsync(socket);
    }

    [Fact]
    public async Task CountsRefreshOnControlledTicksAndConnectionLimitRecoversAfterClose()
    {
        var clock = new LiveTestClock();
        await using var root = new StanzaFactory(clock);
        await using var factory = WithLiveStatus(root, clock);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client);
        var sockets = new List<WebSocket>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var socket = await ConnectAsync(factory, cookie);
                sockets.Add(socket);
                Assert.Equal(0, (await ReceiveStatusAsync(socket)).GetProperty("agentCount").GetInt32());
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(factory, cookie));
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
                db.Set<AgentEntity>().Add(new AgentEntity { Id = Guid.NewGuid(), Identity = "new", Hostname = "new", PublicKey = "key", Status = "pending" });
                await db.SaveChangesAsync();
            }
            clock.Advance(TimeSpan.FromSeconds(15));
            foreach (var socket in sockets)
                Assert.Equal(1, (await ReceiveStatusAsync(socket)).GetProperty("pendingAgentCount").GetInt32());
            await CloseAsync(sockets[0]);
            using var replacement = await ConnectAsync(factory, cookie);
            await ReceiveStatusAsync(replacement);
            await CloseAsync(replacement);
            foreach (var socket in sockets.Skip(1)) await CloseAsync(socket);
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    private static WebApplicationFactory<ControlPlaneEntryPoint> WithLiveStatus(StanzaFactory factory, TimeProvider? clock = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddStanzaLiveStatus();
            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }
        }));

    private static async Task<Guid> BootstrapAsync(WebApplicationFactory<ControlPlaneEntryPoint> factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
    }

    private static async Task<string> LoginAsync(HttpClient client, string username = "owner")
    {
        var html = await client.GetStringAsync("/account/login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["Username"] = username,
            ["Password"] = "a-secure-password-123"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static Task<WebSocket> ConnectAsync(WebApplicationFactory<ControlPlaneEntryPoint> factory, string cookie, string? origin = "http://localhost")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            if (origin is not null) request.Headers.Origin = origin;
        };
        return client.ConnectAsync(new Uri("ws://localhost/live/status"), CancellationToken.None);
    }

    private static Task<HttpResponseMessage> RequestFeedAsync(HttpClient client, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/live/status");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReceiveStatusAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        return json.RootElement.Clone();
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }
}
