using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class AutomationHttpTests
{
    internal static WebApplicationFactory<Stanza.ControlPlane.ControlPlaneEntryPoint> Configure(StanzaFactory original) => original.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.AddStanzaAutomation();
            services.RemoveAll<IOperationService>(); services.AddSingleton<IOperationService, FakeOperations>();
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AutomationWorker)).ToArray()) services.Remove(descriptor);
        }));

    [Fact]
    public async Task EditorsRenderAndActionsSchedulesImagesSaveWithCsrf()
    {
        await using var original = new StanzaFactory(); await using var factory = Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Guid owner;
        using (var scope = factory.Services.CreateScope()) owner = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
        await Login(client, "owner");
        foreach (var route in new[] { "/automation", "/automation/actions/edit", "/automation/schedules/edit", "/automation/webhooks/edit", "/automation/images/edit", "/automation/limits" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/automation/actions/edit", Form(("Name", "no csrf")))).StatusCode);
        var deployment = Guid.NewGuid();
        var csrf = await Token(client, "/automation/actions/edit");
        var saved = await client.PostAsync("/automation/actions/edit", Form(("__RequestVerificationToken", csrf), ("Name", "Browser action"),
            ("Enabled", "true"), ("Concurrency", "queue"), ("PlanJson", JsonSerializer.Serialize(new ActionPlan([new("validate", deployment)]), AutomationRules.Json))));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Browser action", await client.GetStringAsync("/automation"));
        csrf = await Token(client, "/automation/schedules/edit");
        var schedule = await client.PostAsync("/automation/schedules/edit", Form(("__RequestVerificationToken", csrf), ("Name", "Browser schedule"),
            ("DeploymentId", deployment.ToString()), ("Expression", "*/15 * * * *"), ("TimeZone", "America/New_York"), ("Overlap", "skip"), ("MissedRun", "run-once"), ("Enabled", "true"), ("Operation", "deploy")));
        Assert.Equal(HttpStatusCode.Redirect, schedule.StatusCode);
        var preview = await client.PostAsync("/automation/schedules/preview", Form(("__RequestVerificationToken", csrf), ("Expression", "*/15 * * * *"), ("TimeZone", "UTC")));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode); Assert.Contains("Upcoming occurrences", await preview.Content.ReadAsStringAsync());
        csrf = await Token(client, "/automation/images/edit");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/automation/images/edit", Form(("__RequestVerificationToken", csrf), ("Name", "Browser images"),
            ("DeploymentId", deployment.ToString()), ("Services", "app,worker"), ("Mode", "notify-only"), ("FrequencyMinutes", "15"), ("FailureBackoffMinutes", "60")))).StatusCode);
        var overview = await client.GetStringAsync("/automation"); Assert.Contains("Browser schedule", overview); Assert.Contains("Browser images", overview);
        Assert.DoesNotContain("Protected", overview);
        using var readScope = factory.Services.CreateScope();
        var list = await readScope.ServiceProvider.GetRequiredService<AutomationService>().ListAsync(owner);
        var action = Assert.Single(list.Actions);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/automation/actions/edit?id={action.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/automation/actions/{action.Id}/run", Form())).StatusCode);
        csrf = await Token(client, $"/automation/actions/edit?id={action.Id}");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync($"/automation/actions/{action.Id}/run", Form(("__RequestVerificationToken", csrf), ("parametersJson", "{}")))).StatusCode);
    }

    [Fact]
    public async Task WebhookSecretIsShownOnceAndSignedEndpointNeedsNoBrowserSessionOrCsrf()
    {
        await using var original = new StanzaFactory(); await using var factory = Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123");
        await Login(client, "owner"); var csrf = await Token(client, "/automation/webhooks/edit");
        var saved = await client.PostAsync("/automation/webhooks/edit", Form(("__RequestVerificationToken", csrf), ("Name", "Signed hook"),
            ("DeploymentId", Guid.NewGuid().ToString()), ("Provider", "generic"), ("Operation", "deploy"), ("Events", "push"), ("ParameterMapping", "{}"), ("Enabled", "true")));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); var secretHtml = await saved.Content.ReadAsStringAsync();
        var secret = Regex.Match(secretHtml, "<pre>([a-f0-9]{64})</pre>").Groups[1].Value; Assert.Equal(64, secret.Length);
        var id = Guid.Parse(Regex.Match(secretHtml, "/hooks/([a-f0-9-]{36})").Groups[1].Value);
        Assert.DoesNotContain(secret, await client.GetStringAsync("/automation"));
        Assert.DoesNotContain(secret, await client.GetStringAsync($"/automation/webhooks/edit?id={id}"));
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/{id}") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Stanza-Signature-256", WebhookTests.Sign(secret, body)); request.Headers.Add("X-Stanza-Delivery", "http-one"); request.Headers.Add("X-Stanza-Event", "push");
        var accepted = await anonymous.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Contains("runId", await accepted.Content.ReadAsStringAsync());
        var deliveries = await client.GetStringAsync($"/automation/webhooks/{id}/deliveries"); Assert.Contains("http-one", deliveries); Assert.DoesNotContain(secret, deliveries);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/automation/webhooks/{id}/secret", Form(("revoke", "true")))).StatusCode);
    }

    [Fact]
    public async Task UnprivilegedUsersCannotReadEditOrTriggerOtherAutomation()
    {
        await using var original = new StanzaFactory(); await using var factory = Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid action, hook;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var root = await accounts.BootstrapAsync("owner", "a-secure-password-123");
            await accounts.CreateAsync(root.Id, "user", "a-secure-password-123", AccountRole.User);
            var service = scope.ServiceProvider.GetRequiredService<AutomationService>(); var deployment = Guid.NewGuid();
            action = (await service.SaveActionAsync(root.Id, null, 0, "Top secret action", true, "queue", new([new("deploy", deployment)]))).Id;
            hook = (await service.SaveWebhookAsync(root.Id, new() { Name = "Top secret hook", DeploymentId = deployment }, 0)).Definition.Id;
        }
        await Login(client, "user");
        Assert.DoesNotContain("Top secret", await client.GetStringAsync("/automation"));
        Assert.Contains("denied", (await client.GetAsync($"/automation/actions/edit?id={action}")).Headers.Location?.ToString());
        Assert.Contains("denied", (await client.GetAsync($"/automation/webhooks/{hook}/deliveries")).Headers.Location?.ToString());
        Assert.Contains("denied", (await client.GetAsync("/automation/limits")).Headers.Location?.ToString());
        var csrf = await Token(client, "/automation/schedules/edit");
        var denied = await client.PostAsync($"/automation/actions/{action}/run", Form(("__RequestVerificationToken", csrf)));
        Assert.Contains("denied", denied.Headers.Location?.ToString());
    }

    private static async Task Login(HttpClient client, string username)
    {
        var csrf = await Token(client, "/account/login");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/login", Form(("__RequestVerificationToken", csrf), ("Username", username), ("Password", "a-secure-password-123")))).StatusCode);
    }
    private static async Task<string> Token(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\""); Assert.True(match.Success, html);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) => new(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
}
