using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Automation;
using Stanza.Core.Contracts;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Automation;

public sealed class ScriptActionTests
{
    [Fact]
    public async Task HostDockerButtonTargetsAnAgentWithoutInventingADeploymentAndStillRequiresHostAuthorization()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var plan = new ActionEditorModel { AgentId = h.Agent, Script = "docker system prune --force", Confirmation = "Prune unused resources on this Docker host?" }.ToPlan();
        Assert.Null(plan.DeploymentId); Assert.Null(plan.Steps[0].DeploymentId); Assert.Equal(h.Agent, plan.Steps[0].AgentId);
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Docker cleanup", true, "queue", plan));
        var operation = await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>(); var run = await automation.QueueActionAsync(h.Root, action.Id);
            var executor = new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), automation, s.GetRequiredService<AutomationAuthorization>(), new FakeOperations(), new UnavailableImageDigestProbe(), h.Clock);
            Assert.True(await executor.ClaimAsync(run));
            return await s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, null, "docker-script", "automation:" + run.Id.ToString("N"), h.Agent, plan.Steps[0].Execution));
        });
        await h.GrantAsync("agent.read", "agent", h.Agent);
        await Assert.ThrowsAsync<OperationException>(() => h.InScope(s => s.GetRequiredService<OperationService>().EnqueueAsync(new(h.User, null, "docker-script", AgentId: h.Agent, Execution: plan.Steps[0].Execution))));
        await h.StartWorkerAsync(); await h.WaitStateAsync(operation.Id, "Succeeded");
        var dispatched = Assert.Single(h.Dispatcher.Requests);
        Assert.Equal(h.Agent, dispatched.Agent); Assert.Equal(Guid.Empty, dispatched.Request.DeploymentId);
        Assert.Equal("docker system prune --force", dispatched.Request.Execution!.Script);
    }

    [Theory]
    [InlineData("docker-script", null)]
    [InlineData("container-script", "app")]
    public void ScriptEditorProducesOneLiteralScriptWithoutParameterSubstitution(string target, string? service)
    {
        const string script = "printf '%s\\n' \"${HOME}\"\nprintf '%s' '$(literal)'\n";
        var model = new ActionEditorModel { DeploymentId = Guid.NewGuid(), Target = target, Service = service, Script = script };
        var plan = model.ToPlan();
        var step = Assert.Single(plan.Steps);
        Assert.Equal(model.DeploymentId, step.DeploymentId);
        Assert.Equal(script, AutomationRules.Resolve(step, new Dictionary<string, string>()).Execution!.Script);
        Assert.Equal(service, step.Execution!.Service);
        Assert.Empty(step.Execution.Arguments);
        Assert.Null(plan.Parameters);
        Assert.DoesNotContain(script, step.Execution.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t")]
    [InlineData("echo forbidden\0")]
    public void InvalidScriptBodiesAreRejected(string? script) =>
        Assert.False(BashScriptRules.IsValid(script));

    [Fact]
    public void ScriptSizeLimitAndServiceNamesAreConsistent()
    {
        Assert.True(BashScriptRules.IsValid(new('a', BashScriptRules.MaximumLength)));
        Assert.False(BashScriptRules.IsValid(new('a', BashScriptRules.MaximumLength + 1)));
        Assert.True(BashScriptRules.IsServiceName("API.v2-worker"));
        foreach (var name in new[] { "-option", "../app", "app other", "app;command", "", new string('a', 101) })
            Assert.False(BashScriptRules.IsServiceName(name));
    }

    [Fact]
    public void ScriptCannotBecomeAMultistepPlanOrAcceptCommandFields()
    {
        var plan = new ActionEditorModel { DeploymentId = Guid.NewGuid(), Script = "docker ps" }.ToPlan();
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { Steps = [.. plan.Steps, plan.Steps[0]] }));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { Parameters = [new("command")] }));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { DeploymentId = null }));
        var command = plan.Steps[0].Execution!;
        foreach (var invalid in new[] { command with { Executable = "/bin/sh" }, command with { Mode = "exec" },
            command with { Arguments = ["-c", "injected"] }, command with { Container = "arbitrary" },
            command with { User = "root" }, command with { WorkingDirectory = "/" },
            command with { Environment = new Dictionary<string, string> { ["BASH_ENV"] = "startup" } } })
            Assert.Throws<AutomationException>(() => AutomationRules.ValidateScript("docker-script", invalid));
        Assert.Throws<AutomationException>(() => AutomationRules.ValidateScript("container-script", command));
    }

    [Fact]
    public void ExistingActionIsNotConvertedUnlessReplacementIsExplicit()
    {
        var deployment = Guid.NewGuid();
        var legacy = new ActionPlan([new("build", deployment), new("deploy", deployment)], DeploymentId: deployment);
        var model = ActionEditorModel.From(new() { Id = Guid.NewGuid(), Name = "Old action", Revision = 7 }, legacy);
        Assert.True(model.IsLegacy); Assert.Null(model.Script);
        Assert.Equal(legacy.Steps.Count, AutomationRules.ParsePlan(model.PlanJson!).Steps.Count);
        model.PlanJson = null; model.Script = "docker ps";
        Assert.Throws<AutomationException>(() => model.ToPlan(legacy));
        model.ReplaceLegacy = true;
        Assert.Equal("docker-script", Assert.Single(model.ToPlan(legacy).Steps).Kind);
        Assert.Equal(2, legacy.Steps.Count);
    }

    [Fact]
    public void EditingScriptPreservesCustomizedButtonMetadataAndExactBody()
    {
        var model = new ActionEditorModel { DeploymentId = Guid.NewGuid(), Script = "echo original\r\n" };
        var original = model.ToPlan() with { ButtonLabel = "QA", Order = 23, ShowOnOverview = false, Confirmation = "Proceed?" };
        var loaded = ActionEditorModel.From(new() { Name = "Reset" }, original);
        loaded.Script = "echo edited\n";
        var saved = loaded.ToPlan(original);
        Assert.Equal("QA", saved.ButtonLabel); Assert.Equal(23, saved.Order);
        Assert.False(saved.ShowOnOverview); Assert.Equal("Proceed?", saved.Confirmation);
        Assert.Equal(loaded.Script, saved.Steps[0].Execution!.Script);
        loaded.Script = "echo original\n";
        Assert.Equal("echo original\r\n", loaded.ToPlan(original).Steps[0].Execution!.Script);
    }

    [Theory]
    [InlineData("docker-script", null)]
    [InlineData("container-script", "app")]
    public async Task ScriptButtonQueuesAnImmutableAuthorizedOperation(string target, string? service)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var model = new ActionEditorModel { DeploymentId = h.Deployment, Target = target, Service = service, Script = "printf '%s' '${literal}'\n" };
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Script button", true, "queue", model.ToPlan()));
        await h.GrantAsync("action.execute", "action", action.Id);
        var queued = await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            var run = await automation.QueueActionAsync(h.User, action.Id);
            var executor = new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), automation,
                s.GetRequiredService<AutomationAuthorization>(), new FakeOperations(), new UnavailableImageDigestProbe(), h.Clock);
            Assert.True(await executor.ClaimAsync(run));
            return await s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, h.Deployment, target,
                "automation:" + run.Id.ToString("N"), Execution: model.ToPlan().Steps[0].Execution));
        });
        var before = h.Decode(await h.RowAsync(queued.Id));
        await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, action.Id, action.Revision,
            "Script button", true, "queue", model.ToPlan() with { Steps = [new(target, h.Deployment, Execution: before.Execution! with { Script = "echo changed" })] }));
        Assert.Equal(model.Script, h.Decode(await h.RowAsync(queued.Id)).Execution!.Script);
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(target, h.User, execution: before.Execution));
        await h.StartWorkerAsync();
        await h.WaitStateAsync(queued.Id, "Succeeded");
        var request = Assert.Single(h.Dispatcher.Requests).Request;
        Assert.Equal(model.Script, request.Execution!.Script);
        Assert.Equal(target == "container-script", request.Files.Count == 0);
    }

    [Fact]
    public void LegacyWireSerializationRemainsByteCompatibleForReplay()
    {
        var command = new AgentExecution("/bin/echo", ["hello"], Service: "app");
        var json = JsonSerializer.Serialize(command, JsonSerializerOptions.Web);
        Assert.Equal("{\"executable\":\"/bin/echo\",\"arguments\":[\"hello\"],\"service\":\"app\",\"container\":null,\"user\":null,\"workingDirectory\":null,\"environment\":null,\"mode\":\"exec\"}", json);
        Assert.Null(JsonSerializer.Deserialize<AgentExecution>(json, JsonSerializerOptions.Web)!.Script);
    }
}
