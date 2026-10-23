using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class ActionLiveTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LiveFeedsRequireSameOriginAndCurrentSessionAndNeverTransmitPrivateInputs(bool overview)
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        Guid root, user, runId, grantId;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            root = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            user = (await accounts.CreateAsync(root, "qa", "a-secure-password-123", AccountRole.User)).Id;
            var automation = scope.ServiceProvider.GetRequiredService<AutomationService>();
            var action = await automation.SaveActionAsync(root, null, 0, "Reset DB", true, "queue", new([new("restart", Guid.NewGuid())], [new("secret")]));
            grantId = (await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(root, user, null, "action.execute", "action", action.Id)).Id;
            runId = (await automation.QueueActionAsync(user, action.Id, new Dictionary<string, string> { ["secret"] = "private-input" })).Id;
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/account/login");
        var token = WebUtility.HtmlDecode(Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["Username"] = "qa", ["Password"] = "a-secure-password-123" }));
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        var path = overview ? "/live/deployment-actions" : "/live/actions/" + runId;
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "https://evil.test"; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri("ws://localhost" + path), default));
        sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "http://localhost"; };
        using var socket = await sockets.ConnectAsync(new Uri("ws://localhost" + path), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var buffer = new byte[256];
        var initial = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, initial.MessageType);
        var text = Encoding.UTF8.GetString(buffer, 0, initial.Count); Assert.DoesNotContain("private-input", text);
        using var json = JsonDocument.Parse(text); Assert.Equal(64, json.RootElement.GetProperty("version").GetString()!.Length);
        if (!overview)
        {
            using var update = factory.Services.CreateScope();
            await update.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().Where(x => x.Id == runId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "succeeded"));
            var changed = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, changed.MessageType);
            Assert.NotEqual(text, Encoding.UTF8.GetString(buffer, 0, changed.Count));
        }
        using (var revoke = factory.Services.CreateScope()) await revoke.ServiceProvider.GetRequiredService<AccessService>().RemoveGrantAsync(root, grantId);
        var closed = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }
}
