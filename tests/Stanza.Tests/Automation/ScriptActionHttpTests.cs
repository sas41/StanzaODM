using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;
using Stanza.Tests.Web;

namespace Stanza.Tests.Automation;

public sealed class ScriptActionHttpTests
{
    [Fact]
    public async Task HostMaintenanceEditorAcceptsAnAgentWithNoDeployment()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); var agent = Guid.NewGuid(); Guid root;
        using (var scope = factory.Services.CreateScope())
        {
            root = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            var db = scope.ServiceProvider.GetRequiredService<Stanza.ControlPlane.Persistence.StanzaDbContext>();
            db.Add(new Stanza.ControlPlane.Agents.AgentEntity { Id = agent, Identity = agent.ToString("N"), Hostname = "Docker host", Status = "approved" });
            await db.SaveChangesAsync();
        }
        await AutomationHttpTests.Login(client, "owner");
        var page = await client.GetStringAsync("/automation/actions/edit?agentId=" + agent);
        Assert.Contains("name=\"AgentId\"", page); Assert.Contains("Docker host", page);
        var csrf = await AutomationHttpTests.Token(client, "/automation/actions/edit");
        var saved = await client.PostAsync("/automation/actions/edit", AutomationHttpTests.Form(("__RequestVerificationToken", csrf), ("Name", "Docker cleanup"),
            ("AgentId", agent.ToString()), ("Target", "docker-script"), ("Script", "docker system prune --force"), ("Enabled", "true")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var check = factory.Services.CreateScope(); var automation = check.ServiceProvider.GetRequiredService<AutomationService>();
        var action = Assert.Single((await automation.ListAsync(root)).Actions);
        var plan = (await automation.ReadActionAsync(root, action.Id)).Plan;
        Assert.Null(plan.DeploymentId); Assert.Equal(agent, Assert.Single(plan.Steps).AgentId);
    }

    [Theory]
    [InlineData("docker-script", "")]
    [InlineData("container-script", "app")]
    public async Task WorkflowEditorKeepsScriptSubmissionsCompatibleAndShowsButtonOnDeployment(string target, string service)
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid root, deployment;
        using (var scope = factory.Services.CreateScope())
        {
            root = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            deployment = (await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
        }
        await AutomationHttpTests.Login(client, "owner");
        var editor = await client.GetStringAsync("/automation/actions/edit?deploymentId=" + deployment);
        Assert.Contains("data-workflow-editor", editor); Assert.Contains("data-workflow-add=\"step\"", editor);
        Assert.Contains("action-editor.js", editor); Assert.Contains("name=\"PlanJson\"", editor);
        Assert.DoesNotContain("Replace its steps", editor);
        var csrf = await AutomationHttpTests.Token(client, "/automation/actions/edit");
        const string script = "printf '%s' \"${HOME}\"\n";
        var saved = await client.PostAsync("/automation/actions/edit", AutomationHttpTests.Form(("__RequestVerificationToken", csrf),
            ("Name", "QA button"), ("Enabled", "true"), ("DeploymentId", deployment.ToString()), ("Target", target), ("Service", service), ("Script", script)));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using var verify = factory.Services.CreateScope(); var automation = verify.ServiceProvider.GetRequiredService<AutomationService>();
        var action = Assert.Single((await automation.ListAsync(root)).Actions);
        var plan = (await automation.ReadActionAsync(root, action.Id)).Plan;
        Assert.Equal(script, Assert.Single(plan.Steps).Execution!.Script); Assert.Equal(target, plan.Steps[0].Kind);
        var details = await client.GetStringAsync($"/deployments/{deployment}");
        Assert.Contains($"/automation/actions/{action.Id}/submit", details); Assert.Contains("QA button", details);
        Assert.DoesNotContain(script, details);
        var loaded = await client.GetStringAsync($"/automation/actions/edit?id={action.Id}");
        Assert.DoesNotContain("previous step format", loaded);
    }

    [Fact]
    public async Task LegacyReplacementRequiresConfirmationAndPreservesStoredDefinitionOnFailure()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid root, deployment; ActionDefinition action;
        using (var scope = factory.Services.CreateScope())
        {
            root = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            deployment = (await scope.ServiceProvider.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
            action = await scope.ServiceProvider.GetRequiredService<AutomationService>().SaveActionAsync(root, null, 0, "Legacy", true, "reject",
                new([new("build", deployment), new("deploy", deployment)], DeploymentId: deployment, ButtonLabel: "Custom label", Order: 47));
        }
        await AutomationHttpTests.Login(client, "owner"); var csrf = await AutomationHttpTests.Token(client, $"/automation/actions/edit?id={action.Id}");
        var fields = new[] { ("__RequestVerificationToken", csrf), ("Id", action.Id.ToString()), ("Revision", "1"), ("Name", "Script"),
            ("Enabled", "true"), ("DeploymentId", deployment.ToString()), ("Target", "docker-script"), ("Script", "docker ps") };
        var rejected = await client.PostAsync("/automation/actions/edit", AutomationHttpTests.Form(fields));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode); Assert.Contains("Confirm replacing", await rejected.Content.ReadAsStringAsync());
        using (var check = factory.Services.CreateScope())
        {
            var stored = await check.ServiceProvider.GetRequiredService<AutomationService>().ReadActionAsync(root, action.Id);
            Assert.Equal(action.ProtectedSteps, stored.Definition.ProtectedSteps); Assert.Equal(1, stored.Definition.Revision);
        }
        var accepted = await client.PostAsync("/automation/actions/edit", AutomationHttpTests.Form([.. fields, ("ReplaceLegacy", "true")]));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        using var verify = factory.Services.CreateScope();
        var saved = await verify.ServiceProvider.GetRequiredService<AutomationService>().ReadActionAsync(root, action.Id);
        Assert.Equal("docker-script", Assert.Single(saved.Plan.Steps).Kind);
        Assert.Equal("reject", saved.Definition.Concurrency); Assert.Equal("Custom label", saved.Plan.ButtonLabel); Assert.Equal(47, saved.Plan.Order);
    }

    [Fact]
    public async Task DeploymentButtonsHonorGroupAccessAndDoNotExposeScripts()
    {
        await using var original = new StanzaFactory(); await using var factory = AutomationHttpTests.Configure(original);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false }); Guid deployment, allowed, denied;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider; var accounts = sp.GetRequiredService<AccountService>();
            var root = (await accounts.BootstrapAsync("owner", "a-secure-password-123")).Id;
            var user = (await accounts.CreateAsync(root, "qa", "a-secure-password-123", AccountRole.User)).Id;
            deployment = (await sp.GetRequiredService<DeploymentDraftService>().CreateAsync(root, "Application", null, "services: {}", "")).Id;
            var plan = new ActionEditorModel { DeploymentId = deployment, Target = "container-script", Service = "app", Script = "echo sensitive-script" }.ToPlan();
            var automation = sp.GetRequiredService<AutomationService>();
            allowed = (await automation.SaveActionAsync(root, null, 0, "Allowed button", true, "queue", plan)).Id;
            denied = (await automation.SaveActionAsync(root, null, 0, "Denied button", true, "queue", plan)).Id;
            var access = sp.GetRequiredService<AccessService>(); var group = await access.CreateGroupAsync(root, "QA");
            await access.AddMemberAsync(root, group.Id, user);
            await access.AddGrantAsync(root, null, group.Id, "deployment.read", "deployment", deployment);
            await access.AddGrantAsync(root, null, group.Id, "action.execute", "action", allowed);
        }
        await AutomationHttpTests.Login(client, "qa");
        var details = await client.GetStringAsync($"/deployments/{deployment}");
        Assert.Contains($"/automation/actions/{allowed}/submit", details); Assert.DoesNotContain(denied.ToString(), details);
        Assert.DoesNotContain("sensitive-script", details); Assert.DoesNotContain("Add action", details);
    }
}
