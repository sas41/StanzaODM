using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Configuration;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class ActionRunOutputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunFeedStreamsStepOutputsResumesAndNeverBorrowsTheOwnersOutputPermissions(bool canReadOutput)
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        Guid owner, viewer, runId, actionId, revokedGrant; var deployment = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; var accounts = sp.GetRequiredService<AccountService>();
            owner = (await accounts.BootstrapAsync("owner", ConfigurationTestStore.Password)).Id;
            viewer = (await accounts.CreateAsync(owner, "viewer", ConfigurationTestStore.Password, AccountRole.User)).Id;
            var automation = sp.GetRequiredService<AutomationService>();
            var action = await automation.SaveActionAsync(owner, null, 0, "Deploy", true, "queue", new([new("deploy", deployment), new("restart", deployment)], [new("secret")], deployment));
            actionId = action.Id;
            revokedGrant = (await sp.GetRequiredService<AccessService>().AddGrantAsync(owner, viewer, null, "action.execute", "action", action.Id)).Id;
            if (canReadOutput) foreach (var permission in new[] { "deployment.read", "logs.read" })
            {
                var grant = await sp.GetRequiredService<AccessService>().AddGrantAsync(owner, viewer, null, permission, "deployment", deployment);
                if (permission == "logs.read") revokedGrant = grant.Id;
            }
            runId = (await automation.QueueActionAsync(owner, action.Id, new Dictionary<string, string> { ["secret"] = "private-action-input" })).Id;
            var db = sp.GetRequiredService<StanzaDbContext>();
            db.AddRange(new OperationEntity { Id = first, DeploymentId = deployment, Kind = "deploy", State = "Running", Stage = "Pulling", LeaseUntil = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                new OperationEntity { Id = second, DeploymentId = deployment, Kind = "restart", State = "Queued" },
                new AutomationStepRun { RunId = runId, Index = 0, Kind = "deploy", DeploymentId = deployment, OperationId = first, State = "running" },
                new AutomationStepRun { RunId = runId, Index = 1, Kind = "restart", DeploymentId = deployment, OperationId = second, State = "queued" });
            await db.SaveChangesAsync();
            await db.Set<AutomationRun>().Where(x => x.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "running").SetProperty(x => x.OperationId, first));
            for (var i = 1; i <= 10; i++) await sp.GetRequiredService<OperationOutputStore>().AppendAsync(first, new(i, "stdout", $"live-output-{i}\n"));
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var token = await ConfigurationHttp.TokenAsync(client, "/account/login");
        var login = await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", token), ("Username", "viewer"), ("Password", ConfigurationTestStore.Password)));
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        var sockets = factory.Server.CreateWebSocketClient(); sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "https://evil.test"; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri($"ws://localhost/live/actions/{runId}/output"), default));
        sockets.ConfigureRequest = r => { r.Headers.Cookie = cookie; r.Headers.Origin = "http://localhost"; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri($"ws://localhost/live/actions/{runId}/output?after=invalid"), default));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await sockets.ConnectAsync(new Uri($"ws://localhost/live/actions/{runId}/output"), timeout.Token);
        var initial = await ReceiveAsync(socket, timeout.Token);
        Assert.Equal("running", initial.GetProperty("state").GetString());
        Assert.DoesNotContain("private-action-input", initial.GetRawText());
        var operation = initial.GetProperty("operations").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == first);
        Assert.Equal("Pulling", operation.GetProperty("stage").GetString());
        Assert.Equal(canReadOutput, operation.GetProperty("outputAllowed").GetBoolean());
        Assert.Equal(canReadOutput ? 8 : 0, operation.GetProperty("chunks").GetArrayLength());
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AccessService>().RemoveGrantAsync(owner, revokedGrant);
        var closeBuffer = new byte[65536];
        System.Net.WebSockets.WebSocketReceiveResult closed;
        do { closed = await socket.ReceiveAsync(closeBuffer, timeout.Token); } while (closed.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        socket.Abort();
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AccessService>().AddGrantAsync(owner, viewer, null,
            canReadOutput ? "logs.read" : "action.execute", canReadOutput ? "deployment" : "action", canReadOutput ? deployment : actionId);
        token = await ConfigurationHttp.TokenAsync(client, "/account/login");
        login = await client.PostAsync("/account/login", ConfigurationHttp.Form(("__RequestVerificationToken", token), ("Username", "viewer"), ("Password", ConfigurationTestStore.Password)));
        cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Stanza.Session=", StringComparison.Ordinal)).Split(';')[0];
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; var db = sp.GetRequiredService<StanzaDbContext>();
            await sp.GetRequiredService<OperationOutputStore>().AppendAsync(second, new(1, "stderr", "actual failure detail\n"));
            await db.Set<OperationEntity>().Where(x => x.Id == first).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "Succeeded"));
            await db.Set<OperationEntity>().Where(x => x.Id == second).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "Failed").SetProperty(x => x.Code, "runtime_failed"));
            await db.Set<AutomationRun>().Where(x => x.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "failed").SetProperty(x => x.OperationId, second));
        }
        var after = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<Guid, long> { [first] = 8 }));
        using var resumed = await sockets.ConnectAsync(new Uri($"ws://localhost/live/actions/{runId}/output?after={after}"), timeout.Token);
        var final = await ReceiveAsync(resumed, timeout.Token);
        Assert.Equal("failed", final.GetProperty("state").GetString());
        var outputs = final.GetProperty("operations").EnumerateArray().ToArray();
        if (canReadOutput)
        {
            Assert.Equal(new long[] { 9, 10 }, outputs.Single(x => x.GetProperty("id").GetGuid() == first).GetProperty("chunks").EnumerateArray().Select(x => x.GetProperty("sequence").GetInt64()));
            Assert.Contains("actual failure detail", final.GetRawText());
        }
        else { Assert.DoesNotContain("actual failure detail", final.GetRawText()); Assert.DoesNotContain("live-output", final.GetRawText()); }
        resumed.Abort();
        Assert.Contains("Live execution feed", await client.GetStringAsync($"/automation/runs/{runId}"));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct); Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            data.Write(buffer, 0, result.Count); if (result.EndOfMessage) break;
        }
        using var json = JsonDocument.Parse(data.ToArray()); return json.RootElement.Clone();
    }
}
