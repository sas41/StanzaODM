using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Logs;

public sealed class LogPageTests
{
    [Fact]
    public async Task AuthorizedStoppedContainerPageHasSelfHostedReadOnlyControlsAndActionableUnavailableState()
    {
        await using var server = await LogTestServer.CreateAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(server.Origin + "/logs/" + server.Deployment)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", await server.CookieAsync());
        await server.MutateAsync(async db => (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!.ObservedState = "stopped");
        using var response = await client.GetAsync(server.Origin + "/logs/" + server.Deployment);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("/js/logs.js", html);
        Assert.Contains("observed state: stopped", html);
        Assert.Contains("last 200 lines", html);
        Assert.Contains("256 KiB", html);
        Assert.Contains("<pre id=\"container-logs\"", html);
        Assert.Contains("name=\"targetType\"", html);
        Assert.DoesNotContain("name=\"executable\"", html);
        Assert.DoesNotContain("name=\"arguments\"", html);
        Assert.DoesNotContain("/vendor/xterm", html);
        Assert.DoesNotContain("cdn.jsdelivr", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(server.Origin + "/js/logs.js")).StatusCode);
        await server.MutateAsync(async db => (await db.Set<DeploymentDraftEntity>().FindAsync(server.Deployment))!.TargetAgentId = null);
        using var unavailable = await client.GetAsync(server.Origin + "/logs/" + server.Deployment);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Contains("currently assigned Agent", await unavailable.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync(server.Origin + "/logs/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PageDeniesUsersWithoutLogPermission()
    {
        await using var server = await LogTestServer.CreateAsync();
        Guid reader;
        using (var scope = server.App.Services.CreateScope())
            reader = (await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(server.Owner, "reader", "a-secure-password-123", AccountRole.User)).Id;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", await server.CookieAsync(reader));
        using var response = await client.GetAsync(server.Origin + "/logs/" + server.Deployment);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ForwardedHeadersAndQueryCredentialsCannotBypassOriginOrCookieAuthentication()
    {
        await using var server = await LogTestServer.CreateAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var anonymous = await client.GetAsync(server.Origin + "/live/logs/" + server.Deployment + "?token=fake&access_token=fake");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", await server.CookieAsync());
        using var forged = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/live/logs/" + server.Deployment);
        forged.Headers.Add("Origin", "https://forged.example");
        forged.Headers.Add("X-Forwarded-Host", "forged.example");
        forged.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forged)).StatusCode);
        using var direct = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/live/logs/" + server.Deployment);
        direct.Headers.Add("Origin", server.Origin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(direct)).StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stamp")]
    [InlineData("expired")]
    public async Task StaleCookiesCannotUpgradeBeforeOpeningTransport(string change)
    {
        await using var server = await LogTestServer.CreateAsync();
        var cookie = await server.CookieAsync();
        await server.MutateAsync(async db =>
        {
            var account = (await db.Accounts.FindAsync(server.Owner))!;
            if (change == "disabled") account.Enabled = false;
            if (change == "stamp") account.SecurityStamp = "different";
        });
        if (change == "expired") server.Clock.Advance(TimeSpan.FromHours(9));
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync(cookie));
        Assert.Empty(server.Transport.Opens);
    }

    [Theory]
    [InlineData("{\"type\":\"open\"}")]
    [InlineData("{\"type\":\"open\",\"service\":\"web\",\"container\":\"other\"}")]
    [InlineData("{\"type\":\"open\",\"service\":\"web\",\"mode\":\"exec\"}")]
    [InlineData("{\"type\":\"open\",\"service\":\"web\",\"executable\":\"/bin/sh\"}")]
    public async Task InvalidOpeningCannotTurnLogStreamIntoExecOrReachTransport(string json)
    {
        await using var server = await LogTestServer.CreateAsync();
        using var socket = await server.ConnectAsync(await server.CookieAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
        var close = await socket.ReceiveAsync(new byte[256], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        Assert.Empty(server.Transport.Opens);
    }
}
