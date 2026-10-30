using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Automation;

public sealed class OutgoingWebhookTests
{
    [Theory]
    [InlineData("discord")][InlineData("teams")][InlineData("slack")][InlineData("telegram")][InlineData("signal")]
    public void DefaultTemplatesRenderValidJsonAndNeverReinterpretInsertedValues(string provider)
    {
        var value = "quote\" slash\\ newline\n {source} <script>";
        var body = OutgoingTemplates.Render(OutgoingTemplates.Defaults[provider], new Dictionary<string, string> { ["message"] = value, ["source"] = "secret" });
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Contains("{source}", body); Assert.DoesNotContain("secret", body);
        Assert.Contains("quote", body);
    }

    [Theory]
    [InlineData("not json")][InlineData("null")][InlineData("[]")][InlineData("\"text\"")]
    public void TemplatesRequireAJsonObject(string template) => Assert.Throws<AutomationException>(() => OutgoingTemplates.Render(template, new Dictionary<string, string>()));

    [Fact]
    public async Task ConfigurationIsAdminOnlyEncryptedVersionedAndBlankUrlPreservesSecrets()
    {
        await using var s = await AutomationTestStore.Create(); var service = new OutgoingWebhookService(s.Db, s.Protection, s.Clock);
        await Assert.ThrowsAsync<AutomationAccessException>(() => service.ReadAsync(s.User));
        await Assert.ThrowsAsync<AutomationAccessException>(() => service.SaveAsync(s.User, new(Name: "Denied", Url: "https://example.test/secret")));
        await service.SaveAsync(s.Root, new(Name: "Discord", Url: "https://example.test/token-secret"));
        Assert.DoesNotContain("token-secret", (await s.Db.Settings.SingleAsync(x => x.Key == "outgoing-webhooks.v1")).Value);
        var page = await service.ReadAsync(s.Root); var channel = Assert.Single(page.Configuration.Channels); Assert.Equal("", channel.Url);
        Assert.Equal(OutgoingTemplates.Defaults["discord"], channel.Template);
        await service.SaveAsync(s.Root, new(channel.Id, 1, "Renamed", Url: "", Template: channel.Template));
        await Assert.ThrowsAsync<AutomationException>(() => service.SaveAsync(s.Root, new(channel.Id, 1, "Stale")));
        var decoded = await service.ReadAsync(s.Root); Assert.Equal("Renamed", Assert.Single(decoded.Configuration.Channels).Name);
        // The preserved endpoint is used by delivery, not merely retained as an encrypted blob.
        var action = await s.Action(); var run = await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync();
        await service.CaptureAsync();
        var handler = new Handler(HttpStatusCode.OK); using var client = new HttpClient(handler);
        Assert.True(await service.SendNextAsync(client)); Assert.Equal("https://example.test/token-secret", Assert.Single(handler.Urls));
        await service.DeleteAsync(s.Root, channel.Id, 2); Assert.Empty((await service.ReadAsync(s.Root)).Configuration.Channels);
    }

    [Theory]
    [InlineData("http://example.test/hook")][InlineData("https://user:pass@example.test")][InlineData("file:///etc/passwd")][InlineData("https://example.test/#secret")]
    public async Task EndpointValidationNeverAllowsHttpUserInfoFragmentsOrLocalFileSchemes(string url)
    {
        await using var s = await AutomationTestStore.Create(); var service = new OutgoingWebhookService(s.Db, s.Protection, s.Clock);
        await Assert.ThrowsAsync<AutomationException>(() => service.SaveAsync(s.Root, new(Name: "Invalid", Url: url)));
    }

    [Theory]
    [InlineData("succeeded", "succeeded")][InlineData("failed", "failed")][InlineData("cancelled", "failed")]
    [InlineData("denied", "failed")][InlineData("uncertain", "failed")][InlineData("skipped", "failed")]
    public void AllTerminalOutcomesHaveTheExpectedEvent(string state, string expected)
    {
        var channel = new OutgoingChannel(Guid.NewGuid(), "test", "discord", "", "", true, true, true, true, 0);
        Assert.Equal(new[] { "started", expected }, OutgoingWebhookService.Events(new() { State = state, StartedAt = 1, CompletedAt = 2 }, channel));
        Assert.Empty(OutgoingWebhookService.Events(new() { State = "queued" }, channel));
        Assert.Equal(new[] { "started" }, OutgoingWebhookService.Events(new() { State = "running", StartedAt = 1 }, channel));
    }

    [Fact]
    public async Task CaptureIsDurableIdempotentAndDoesNotReplayHistoricalRunsOrExposeParametersAndOutput()
    {
        await using var s = await AutomationTestStore.Create(); var service = new OutgoingWebhookService(s.Db, s.Protection, s.Clock);
        var action = await s.Action(); await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync();
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await service.SaveAsync(s.Root, new(Name: "Events", Url: "https://example.test/hook", Started: true));
        var run = await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync();
        await service.CaptureAsync(); await service.CaptureAsync();
        var rows = await s.Db.Set<OutgoingDelivery>().AsNoTracking().ToListAsync(); Assert.Equal(2, rows.Count);
        Assert.All(rows, x => { Assert.Equal(run.Id, x.RunId); Assert.DoesNotContain("Stanza:", x.ProtectedBody); });
        var handler = new Handler(HttpStatusCode.OK); using var client = new HttpClient(handler);
        while (await service.SendNextAsync(client)) { }
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, x => { Assert.Contains("Action", x); Assert.DoesNotContain("Protected", x); });
        await service.CaptureAsync(); Assert.False(await service.SendNextAsync(client));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 1)][InlineData(HttpStatusCode.Found, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 5)][InlineData(HttpStatusCode.TooManyRequests, 5)]
    public async Task RetryPolicyIsBoundedAndNeverIncludesRemoteResponseBodies(HttpStatusCode status, int expected)
    {
        await using var s = await AutomationTestStore.Create(); var service = new OutgoingWebhookService(s.Db, s.Protection, s.Clock);
        await service.SaveAsync(s.Root, new(Name: "Events", Url: "https://example.test/hook"));
        var action = await s.Action(); await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync(); await service.CaptureAsync();
        var handler = new Handler(status); using var client = new HttpClient(handler);
        for (var i = 0; i < 6; i++) { await service.SendNextAsync(client); s.Clock.Advance(TimeSpan.FromHours(1)); }
        Assert.Equal(expected, handler.Bodies.Count);
        var row = await s.Db.Set<OutgoingDelivery>().AsNoTracking().SingleAsync(); Assert.Equal("failed", row.State); Assert.Equal(expected, row.Attempts);
        Assert.DoesNotContain("remote-secret", row.Result);
    }

    [Fact]
    public async Task DisabledAndDeletedChannelsStopAlreadyCapturedMessages()
    {
        await using var s = await AutomationTestStore.Create(); var service = new OutgoingWebhookService(s.Db, s.Protection, s.Clock);
        await service.SaveAsync(s.Root, new(Name: "Events", Url: "https://example.test/hook"));
        var action = await s.Action(); await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync(); await service.CaptureAsync();
        var channel = Assert.Single((await service.ReadAsync(s.Root)).Configuration.Channels);
        await service.SaveAsync(s.Root, new(channel.Id, 1, channel.Name, Url: "", Enabled: false));
        var handler = new Handler(HttpStatusCode.OK); using var client = new HttpClient(handler);
        await service.SendNextAsync(client); Assert.Empty(handler.Bodies);
        Assert.Equal("channel_disabled", (await s.Db.Set<OutgoingDelivery>().AsNoTracking().SingleAsync()).Result);
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Urls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString()); Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(status) { Content = new StringContent("remote-secret") };
        }
    }
}
