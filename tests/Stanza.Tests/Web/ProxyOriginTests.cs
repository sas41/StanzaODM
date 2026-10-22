using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;

namespace Stanza.Tests.Web;

public sealed class ProxyOriginTests
{
    [Fact]
    public async Task ConfiguredPublicOriginSupportsTlsTerminationWithoutTrustingForwardedHeaders()
    {
        await using var factory = new StanzaFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var owner = await factory.BootstrapAsync();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InstanceSettingsService>()
                .SaveAsync(owner, new("https://stanza.example.com", "SECRET=not-transport-metadata", 0));
        var html = await client.GetStringAsync("/account/login");
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Username"] = "owner", ["Password"] = "a-secure-password-123"
        }));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "https://stanza.example.com";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await sockets.ConnectAsync(new Uri("ws://localhost/live/status"), timeout.Token);
        var bytes = new byte[256];
        var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        using var status = JsonDocument.Parse(bytes.AsMemory(0, received.Count));
        Assert.True(status.RootElement.GetProperty("databaseReady").GetBoolean());
        Assert.DoesNotContain("not-transport-metadata", System.Text.Encoding.UTF8.GetString(bytes, 0, received.Count));
        socket.Abort();
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/live/status");
        forged.Headers.Add("Origin", "https://evil.invalid");
        forged.Headers.Add("X-Forwarded-Host", "evil.invalid");
        forged.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forged, timeout.Token)).StatusCode);
        using var oldOrigin = new HttpRequestMessage(HttpMethod.Get, "/live/status");
        oldOrigin.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(oldOrigin, timeout.Token)).StatusCode);
    }
}
