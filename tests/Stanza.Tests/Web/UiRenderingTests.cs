using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Automation;

namespace Stanza.Tests.Web;

public sealed class UiRenderingTests
{
    [Fact]
    public async Task NamedAutomationSelectorsContainOnlyReadableResourcesAndUseTheSharedNavigation()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        Guid visibleDeployment, visibleAction;
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider; var accounts = services.GetRequiredService<AccountService>();
            var root = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            var qa = (await accounts.CreateAsync(root, "qa", "a-secure-password-123", AccountRole.User)).Id;
            var drafts = services.GetRequiredService<DeploymentDraftService>();
            visibleDeployment = (await drafts.CreateAsync(root, "Visible deployment", null, "services: {}", "")).Id;
            var hidden = (await drafts.CreateAsync(root, "Private deployment not granted", null, "services: {}", "")).Id;
            var automation = services.GetRequiredService<AutomationService>();
            visibleAction = (await automation.SaveActionAsync(root, null, 0, "Visible action", true, "queue", new([new("restart", visibleDeployment)]))).Id;
            await automation.SaveActionAsync(root, null, 0, "Private action not granted", true, "queue", new([new("restart", hidden)]));
            var access = services.GetRequiredService<AccessService>();
            await access.AddGrantAsync(root, qa, null, "deployment.read", "deployment", visibleDeployment);
            await access.AddGrantAsync(root, qa, null, "action.read", "action", visibleAction);
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await Login(client, "qa");
        foreach (var path in new[] { "/automation/schedules/edit", "/automation/webhooks/edit", "/automation/images/edit" })
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains("Visible deployment", html); Assert.Contains(visibleDeployment.ToString(), html);
            Assert.DoesNotContain("Private deployment not granted", html); Assert.DoesNotContain("Private action not granted", html);
            Assert.Matches("<select[^>]*id=\"DeploymentId\"", html);
            if (path != "/automation/images/edit") { Assert.Contains("Visible action", html); Assert.Contains(visibleAction.ToString(), html); }
            Assert.Contains("aria-controls=\"primary-navigation\"", html); Assert.Contains("href=\"#main-content\"", html);
            Assert.Contains("href=\"/automation\" aria-current=\"page\"", html);
        }
    }

    [Fact]
    public async Task InvalidEditorPostsKeepNamedOptionsAndUnavailableSavedSelectionsAreNotDropped()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        Guid owner, deployment, schedule;
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider; owner = (await services.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            deployment = (await services.GetRequiredService<DeploymentDraftService>().CreateAsync(owner, "Named deployment", null, "services: {}", "")).Id;
            schedule = (await services.GetRequiredService<AutomationService>().SaveScheduleAsync(owner, new() { Name = "Saved schedule", DeploymentId = Guid.NewGuid() }, 0)).Id;
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await Login(client, "owner");
        var page = await client.GetStringAsync("/automation/schedules/edit"); var csrf = Token(page);
        var response = await client.PostAsync("/automation/schedules/edit", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = csrf, ["Name"] = "", ["DeploymentId"] = deployment.ToString(), ["Expression"] = "* * * * *", ["TimeZone"] = "UTC" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invalid = await response.Content.ReadAsStringAsync(); Assert.Contains("Named deployment", invalid); Assert.Contains("validation-summary-errors", invalid);
        var saved = await client.GetStringAsync("/automation/schedules/edit?id=" + schedule);
        Assert.Contains("Unavailable current deployment", saved); Assert.Contains("Named deployment", saved);
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task Login(HttpClient client, string name)
    {
        var token = Token(await client.GetStringAsync("/account/login"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["Username"] = name, ["Password"] = "a-secure-password-123" }))).StatusCode);
    }
}
