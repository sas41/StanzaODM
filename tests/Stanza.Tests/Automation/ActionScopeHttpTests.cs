using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Configuration;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class ActionScopeHttpTests
{
    [Fact]
    public async Task ActionsAreGroupedByScopeAndEditableInlineWithoutChangingDeploymentDraft()
    {
        await using var factory = new StanzaFactory(); var root = await factory.BootstrapAsync();
        using var logs = new ConfigurationDiagnosticLogs(); factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        Guid deployment, other; ActionDefinition local, otherAction, global;
        using (var scope = factory.Services.CreateScope())
        {
            var drafts = scope.ServiceProvider.GetRequiredService<DeploymentDraftService>();
            deployment = (await drafts.CreateAsync(root, "Development", null, "services: {}\n", "")).Id;
            other = (await drafts.CreateAsync(root, "Staging", null, "services: {}\n", "")).Id;
            var service = scope.ServiceProvider.GetRequiredService<AutomationService>();
            local = await service.SaveActionAsync(root, null, 0, "Deploy Latest", true, "queue", Plan(deployment));
            otherAction = await service.SaveActionAsync(root, null, 0, "Deploy Latest", true, "queue", Plan(other));
            global = await service.SaveActionAsync(root, null, 0, "Deploy Latest", true, "queue", Plan(deployment) with { DeploymentId = null });
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await ConfigurationHttp.LoginAsync(client, "owner");
        var page = await client.GetStringAsync("/automation");
        Assert.Contains("Global actions", page); Assert.Contains("Development", page); Assert.Contains("Staging", page);
        Assert.Contains($"data-action-scope=\"{deployment}\"", page); Assert.Contains($"data-action-scope=\"{other}\"", page);
        var edit = await client.GetStringAsync($"/deployments/{deployment}/edit");
        Assert.Contains("Deployment actions", edit); Assert.Contains(local.Id.ToString(), edit);
        Assert.DoesNotContain(otherAction.Id.ToString(), edit); Assert.DoesNotContain(global.Id.ToString(), edit);
        var token = await ConfigurationHttp.TokenAsync(client, $"/deployments/{deployment}/edit");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/deployments/{deployment}/actions/save", Fields(local, Plan(deployment), null))).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/deployments/{deployment}/actions/save") { Content = Fields(local, Plan(deployment), token, "Deploy now") };
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join("\n", logs.Errors));
        var partial = await response.Content.ReadAsStringAsync(); Assert.Contains("Action saved", partial); Assert.DoesNotContain("<!DOCTYPE", partial);
        using var verify = factory.Services.CreateScope();
        var saved = await verify.ServiceProvider.GetRequiredService<AutomationService>().ReadActionAsync(root, local.Id);
        Assert.Equal("Deploy now", saved.Definition.Name); Assert.Equal(2, saved.Definition.Revision); Assert.Equal(deployment, saved.Plan.DeploymentId);
        Assert.Equal(1, (await verify.ServiceProvider.GetRequiredService<DeploymentDraftService>().ReadAsync(root, deployment)).Draft.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/deployments/{deployment}/actions/save", Fields(otherAction, Plan(other), token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/deployments/{deployment}/actions/save", Fields(saved.Definition, Plan(other), token))).StatusCode);
        var invalid = await client.PostAsync($"/deployments/{deployment}/actions/save", ConfigurationHttp.Form(("__RequestVerificationToken", token), ("Id", local.Id.ToString()), ("Revision", "2"), ("Name", "Invalid"), ("Concurrency", "queue"), ("PlanJson", "")));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode); Assert.Contains("required", await invalid.Content.ReadAsStringAsync());
        Assert.Equal("Deploy now", (await verify.ServiceProvider.GetRequiredService<AutomationService>().ReadActionAsync(root, local.Id)).Definition.Name);
    }

    [Fact]
    public async Task DeploymentWritePermissionsDoNotGrantActionEditing()
    {
        await using var factory = new StanzaFactory(); var root = await factory.BootstrapAsync(); Guid deployment; ActionDefinition action;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            deployment = (await sp.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
            action = await sp.GetRequiredService<AutomationService>().SaveActionAsync(root, null, 0, "Restart", false, "queue", Plan(deployment));
            var user = (await sp.GetRequiredService<AccountService>().CreateAsync(root, "editor", ConfigurationTestStore.Password, AccountRole.User)).Id;
            foreach (var permission in new[] { "deployment.read", "files.read", "files.write" }) await sp.GetRequiredService<AccessService>().AddGrantAsync(root, user, null, permission, "deployment", deployment);
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); await ConfigurationHttp.LoginAsync(client, "editor");
        var page = await client.GetStringAsync($"/deployments/{deployment}/edit"); Assert.DoesNotContain(action.Id.ToString(), page);
        var token = await ConfigurationHttp.TokenAsync(client, $"/deployments/{deployment}/edit");
        ConfigurationHttp.Denied(await client.PostAsync($"/deployments/{deployment}/actions/save", Fields(action, Plan(deployment), token)));
    }

    private static ActionPlan Plan(Guid deployment) => new([new("deploy", deployment)], DeploymentId: deployment);
    private static FormUrlEncodedContent Fields(ActionDefinition action, ActionPlan plan, string? token, string? name = null) => ConfigurationHttp.Form(
        ("__RequestVerificationToken", token ?? ""), ("Id", action.Id.ToString()), ("Revision", action.Revision.ToString()),
        ("Name", name ?? action.Name), ("Enabled", "true"), ("Concurrency", "queue"), ("PlanJson", JsonSerializer.Serialize(plan, AutomationRules.Json)));
}
