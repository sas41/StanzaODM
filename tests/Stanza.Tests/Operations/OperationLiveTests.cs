using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Configuration;
using Stanza.Tests.Web;

namespace Stanza.Tests.Operations;

public sealed class OperationLiveTests
{
    [Fact]
    public async Task LiveOutputIsAvailableWhileRunningResumesBySequenceAndRespectsRevocation()
    {
        await using var factory = new StanzaFactory(); var owner = await factory.BootstrapAsync();
        var id = Guid.NewGuid(); var deployment = Guid.NewGuid(); Guid user, logsGrant;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            user = (await sp.GetRequiredService<AccountService>().CreateAsync(owner, "viewer", ConfigurationTestStore.Password, AccountRole.User)).Id;
            var access = sp.GetRequiredService<AccessService>();
            await access.AddGrantAsync(owner, user, null, "deployment.read", "deployment", deployment);
            logsGrant = (await access.AddGrantAsync(owner, user, null, "logs.read", "deployment", deployment)).Id;
            var db = sp.GetRequiredService<StanzaDbContext>();
            db.Add(new OperationEntity { Id = id, DeploymentId = deployment, State = "Running", Kind = "deploy",
                LeaseUntil = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
            await sp.GetRequiredService<OperationOutputStore>().AppendAsync(id, new(1, "stdout", "starting\n"));
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var csrf = await ConfigurationHttp.TokenAsync(client, "/account/login");
        var login = await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Username", "viewer"), ("Password", ConfigurationTestStore.Password)));
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "https://evil.example"; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri($"ws://localhost/live/operations/{id}"), default));
        sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "http://localhost"; };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await sockets.ConnectAsync(new Uri($"ws://localhost/live/operations/{id}"), deadline.Token);
        var initial = await ReceiveAsync(socket, deadline.Token);
        Assert.Equal("Running", initial.GetProperty("state").GetString());
        Assert.Equal("starting\n", initial.GetProperty("chunks")[0].GetProperty("text").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationOutputStore>().AppendAsync(id, new(2, "stderr", "actual deployment failure\n"));
            await scope.ServiceProvider.GetRequiredService<AccessService>().RemoveGrantAsync(owner, logsGrant);
        }
        var buffer = new byte[8192];
        WebSocketReceiveResult changed;
        do { changed = await socket.ReceiveAsync(buffer, deadline.Token); } while (changed.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        var denied = await client.GetAsync($"/operations/{id}/output");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/account/login", denied.Headers.Location!.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(owner, user, null, "logs.read", "deployment", deployment);
            await scope.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "Failed").SetProperty(x => x.Code, "runtime_failed"));
        }
        // Grant changes revoke the old session stamp; authenticate again with the new authorization state.
        csrf = await ConfigurationHttp.TokenAsync(client, "/account/login");
        login = await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", csrf), ("Username", "viewer"), ("Password", ConfigurationTestStore.Password)));
        cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        using var resumed = await sockets.ConnectAsync(new Uri($"ws://localhost/live/operations/{id}?after=1"), deadline.Token);
        var replay = await ReceiveAsync(resumed, deadline.Token);
        Assert.Equal("Failed", replay.GetProperty("state").GetString());
        Assert.Equal(2, replay.GetProperty("chunks")[0].GetProperty("sequence").GetInt64());
        Assert.Equal("actual deployment failure\n", replay.GetProperty("chunks")[0].GetProperty("text").GetString());
        Assert.Equal("starting\nactual deployment failure\n", await client.GetStringAsync($"/operations/{id}/output"));
        socket.Abort(); resumed.Abort();
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            data.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        using var json = JsonDocument.Parse(data.ToArray());
        return json.RootElement.Clone();
    }
}
