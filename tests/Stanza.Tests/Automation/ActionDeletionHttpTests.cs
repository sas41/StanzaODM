using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class ActionDeletionHttpTests
{
    [Fact]
    public async Task BothEditorsLinkToConfirmationAndDeletionRequiresCsrfAndExplicitConsent()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid root, deployment; ActionDefinition action;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            root = (await sp.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            deployment = (await sp.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
            action = await sp.GetRequiredService<AutomationService>().SaveActionAsync(root, null, 0, "Delete me", true, "queue",
                new([new("container-script", deployment, Execution: new("/bin/bash", [], Mode: "script", Script: "echo private-script", Services: ["app"], Replicas: "once"))], DeploymentId: deployment));
        }
        await AutomationHttpTests.Login(client, "owner"); var route = $"/automation/actions/{action.Id}/delete";
        Assert.Contains(route, await client.GetStringAsync($"/automation/actions/edit?id={action.Id}"));
        Assert.Contains(route, await client.GetStringAsync($"/deployments/{deployment}/edit"));
        var page = await client.GetStringAsync(route);
        Assert.Contains("Delete me", page); Assert.Contains("name=\"confirmed\"", page); Assert.DoesNotContain("private-script", page);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, AutomationHttpTests.Form(("confirmed", "true")))).StatusCode);
        var csrf = await AutomationHttpTests.Token(client, route);
        var fields = new[] { ("__RequestVerificationToken", csrf), ("revision", action.Revision.ToString()), ("fingerprint", Fingerprint(page)) };
        var unconfirmed = await client.PostAsync(route, AutomationHttpTests.Form(fields));
        Assert.Equal(HttpStatusCode.OK, unconfirmed.StatusCode); Assert.Contains("Confirm that you want", await unconfirmed.Content.ReadAsStringAsync());
        var deleted = await client.PostAsync(route, AutomationHttpTests.Form([.. fields, ("confirmed", "true")]));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode); Assert.Equal("/automation", deleted.Headers.Location?.ToString());
        Assert.Contains("Action deleted", await client.GetStringAsync("/automation"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/automation/actions/edit?id={action.Id}")).StatusCode);
        using var verify = factory.Services.CreateScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
        Assert.Equal(1, (await verify.ServiceProvider.GetRequiredService<DeploymentDraftService>().ReadAsync(root, deployment)).ViewedRevision);
    }

    [Fact]
    public async Task LinkedSchedulesAndWebhooksAreListedAndCannotBeDeletedWithoutSeparateConfirmation()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid root, deployment; ActionDefinition action;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; root = (await sp.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            deployment = (await sp.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
            var automation = sp.GetRequiredService<AutomationService>();
            action = await automation.SaveActionAsync(root, null, 0, "Deploy", true, "queue", new([new("deploy", deployment)], DeploymentId: deployment));
            await automation.SaveActionScheduleAsync(root, action.Id, Guid.Empty, 0, "Nightly deployment", "0 3 * * *", "UTC", true, "skip", "skip");
            await automation.SaveWebhookAsync(root, new() { Name = "Git push", ActionId = action.Id, DeploymentId = deployment, Enabled = true }, 0);
        }
        await AutomationHttpTests.Login(client, "owner"); var route = $"/automation/actions/{action.Id}/delete";
        var page = await client.GetStringAsync(route); var csrf = await AutomationHttpTests.Token(client, route);
        Assert.Contains("Nightly deployment", page); Assert.Contains("Git push", page); Assert.Contains("name=\"deleteLinkedTriggers\"", page);
        var fields = new[] { ("__RequestVerificationToken", csrf), ("revision", action.Revision.ToString()), ("fingerprint", Fingerprint(page)), ("confirmed", "true") };
        var missingConsent = await client.PostAsync(route, AutomationHttpTests.Form(fields));
        Assert.Equal(HttpStatusCode.OK, missingConsent.StatusCode); Assert.Contains("Separately confirm", await missingConsent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(route, AutomationHttpTests.Form([.. fields, ("deleteLinkedTriggers", "true")]))).StatusCode);
        using var verify = factory.Services.CreateScope(); var db = verify.ServiceProvider.GetRequiredService<StanzaDbContext>();
        Assert.False(await db.Set<AutomationSchedule>().AnyAsync()); Assert.False(await db.Set<WebhookDefinition>().AnyAsync());
        Assert.False(await db.Set<ActionDefinition>().AnyAsync());
    }

    [Fact]
    public async Task ExecuteOnlyUserCannotOpenOrSubmitDeletionAndUnrelatedHistoryStaysPrivate()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid action;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; var accounts = sp.GetRequiredService<AccountService>();
            var root = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            var user = (await accounts.CreateAsync(root, "qa", "a-secure-password-123", AccountRole.User)).Id;
            action = (await sp.GetRequiredService<AutomationService>().SaveActionAsync(root, null, 0, "Protected", true, "queue", new([new("deploy", Guid.NewGuid())]))).Id;
            await sp.GetRequiredService<AccessService>().AddGrantAsync(root, user, null, "action.execute", "action", action);
        }
        await AutomationHttpTests.Login(client, "qa"); var route = $"/automation/actions/{action}/delete";
        Assert.Contains("denied", (await client.GetAsync(route)).Headers.Location?.ToString());
        var csrf = await AutomationHttpTests.Token(client, "/account/password");
        var rejected = await client.PostAsync(route, AutomationHttpTests.Form(("__RequestVerificationToken", csrf), ("revision", "1"), ("confirmed", "true")));
        Assert.Contains("denied", rejected.Headers.Location?.ToString());
        using var verify = factory.Services.CreateScope(); Assert.True(await verify.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<ActionDefinition>().AnyAsync(x => x.Id == action));
    }

    [Fact]
    public async Task QueuedWorkDisablesDeletionAndForgedSubmissionStillCannotRemoveTheAction()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); ActionDefinition action;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; var root = (await sp.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            var automation = sp.GetRequiredService<AutomationService>();
            action = await automation.SaveActionAsync(root, null, 0, "Busy", true, "queue", new([new("deploy", Guid.NewGuid())]));
            await automation.QueueActionAsync(root, action.Id);
        }
        await AutomationHttpTests.Login(client, "owner"); var route = $"/automation/actions/{action.Id}/delete";
        var page = await client.GetStringAsync(route); var csrf = await AutomationHttpTests.Token(client, route);
        Assert.Contains("unresolved work", page); Assert.Contains("disabled=\"disabled\"", page);
        var rejected = await client.PostAsync(route, AutomationHttpTests.Form(("__RequestVerificationToken", csrf), ("revision", "1"), ("fingerprint", Fingerprint(page)), ("confirmed", "true")));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode); Assert.Contains("unresolved work", await rejected.Content.ReadAsStringAsync());
        using var verify = factory.Services.CreateScope(); Assert.True(await verify.ServiceProvider.GetRequiredService<StanzaDbContext>().Set<ActionDefinition>().AnyAsync(x => x.Id == action.Id));
    }

    private static string Fingerprint(string page) => Regex.Match(page, "name=\"fingerprint\" value=\"([a-f0-9]{64})\"").Groups[1].Value;
}
