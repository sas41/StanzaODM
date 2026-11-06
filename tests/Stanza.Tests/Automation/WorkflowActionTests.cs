using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Automation;
using Stanza.Core.Contracts;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Automation;

public sealed class WorkflowActionTests
{
    [Fact]
    public async Task DifferentDeploymentsKeepIndependentBranchSelectionsAndDoNotRewriteSourceDefaults()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync();
        var other = await h.AddDeploymentAsync("Other", h.Agent);
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", "[\"main\",\"feature/qa\"]"),
            "resolve-action-inputs" => new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> {
                ["sources/app"] = request.DeploymentId == h.Deployment ? new('a', 40) : new('b', 40) }),
            _ => throw new InvalidOperationException()
        });
        await h.InScope(async s =>
        {
            var sources = s.GetRequiredService<SourceService>();
            await sources.SaveGitAsync(h.Root, other, null, 0, new("https://git.example.test/app.git", "main", "sources/app", null));
            var automation = s.GetRequiredService<AutomationService>();
            foreach (var target in new[] { h.Deployment, other })
            {
                var action = await automation.SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue",
                    new([new("deploy", target), new("container-script", target, Execution: Script())], [new("BRANCH", Input: "branch", Source: "sources/app")], target));
                var branch = target == h.Deployment ? "feature/qa" : "main";
                var run = await automation.QueueActionAsync(h.Root, action.Id, new Dictionary<string, string> { ["BRANCH"] = branch });
                Assert.Equal(branch, (await automation.InvocationAsync(h.Root, action.Id, default)).Values["BRANCH"]);
                Assert.Equal(target == h.Deployment ? new string('a', 40) : new string('b', 40), automation.Unprotect<ActionExecutionBindings>(run.ProtectedBindings).Sources["sources/app"]);
                Assert.Equal("main", Assert.Single((await sources.ReadAsync(h.Root, target)).Git).Reference);
            }
            return true;
        });
    }

    private static AgentExecution Script(string body = "printf '%s' \"$MESSAGE\"") =>
        new("/bin/bash", [], Mode: "script", Script: body, Services: ["app", "worker"], Replicas: "once");

    [Fact]
    public void SingleScriptWorkflowRoundtripDoesNotDiscardItsVariablesOrTargets()
    {
        var deployment = Guid.NewGuid();
        var plan = new ActionPlan([new("container-script", deployment, Execution: Script())], [new("MESSAGE", Default: "remember me")], deployment);
        var model = ActionEditorModel.From(new() { Name = "Script" }, plan);
        Assert.Equal(JsonSerializer.Serialize(plan, AutomationRules.Json), JsonSerializer.Serialize(model.ToPlan(plan), AutomationRules.Json));
    }

    [Fact]
    public void MaximumSizeExistingScriptRemainsEditableWhenJsonEscapingExpandsItsBody()
    {
        var deployment = Guid.NewGuid();
        var body = "#" + new string('é', BashScriptRules.MaximumLength - 1);
        var plan = new ActionPlan([new("container-script", deployment, Execution: Script(body))], DeploymentId: deployment);
        var model = ActionEditorModel.From(new() { Name = "Large script" }, plan);
        Assert.True(model.PlanJson!.Length > 131072);
        Assert.Equal(body, Assert.Single(model.ToPlan(plan).Steps).Execution!.Script);
        Assert.Throws<AutomationException>(() => AutomationRules.ParsePlan(new('a', AutomationRules.MaximumPlanLength + 1)));
    }

    [Theory]
    [InlineData("BASH_ENV")][InlineData("BASH_FUNC_x%%")][InlineData("ENV")][InlineData("PATH")]
    [InlineData("LD_PRELOAD")][InlineData("SHELLOPTS")][InlineData("IFS")][InlineData("STANZA_PROJECT_NAME")]
    [InlineData("DOCKER_HOST")][InlineData("GIT_CONFIG_GLOBAL")][InlineData("bad-name")][InlineData("1NAME")]
    public void ReservedOrInvalidVariablesAreRejectedBySharedRules(string name)
    {
        Assert.False(BashScriptRules.IsVariableName(name));
        var deployment = Guid.NewGuid();
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("container-script", deployment, Execution: Script())], [new(name)], deployment)));
        Assert.False(BashScriptRules.IsEnvironment(new Dictionary<string, string> { [name] = "value" }));
    }

    [Fact]
    public void VariablesAreLiteralEnvironmentValuesAndScriptBodyIsNeverRewritten()
    {
        const string malicious = "'; $(touch /forbidden)\n\"quoted\"";
        var step = new ActionStep("container-script", Guid.NewGuid(), Execution: Script());
        var resolved = AutomationRules.Resolve(step, new Dictionary<string, string> { ["MESSAGE"] = malicious });
        Assert.Equal(step.Execution!.Script, resolved.Execution!.Script);
        Assert.Equal(malicious, resolved.Execution.Environment!["MESSAGE"]);
        Assert.Empty(resolved.Execution.Arguments);
        AutomationRules.ValidateScript("container-script", resolved.Execution);
    }

    [Fact]
    public void VariableEnvironmentRejectsOversizedValuesNulsAndTooManyEntries()
    {
        Assert.True(BashScriptRules.IsEnvironment(new Dictionary<string, string> { ["VALUE"] = new('a', 4096) }));
        Assert.False(BashScriptRules.IsEnvironment(new Dictionary<string, string> { ["VALUE"] = new('a', 4097) }));
        Assert.False(BashScriptRules.IsEnvironment(new Dictionary<string, string> { ["VALUE"] = "nul\0" }));
        Assert.False(BashScriptRules.IsEnvironment(Enumerable.Range(0, 51).ToDictionary(i => "VALUE_" + i, _ => "value")));
        var deployment = Guid.NewGuid();
        var execution = Script() with { Environment = Enumerable.Range(0, 50).ToDictionary(i => "VALUE_" + i, _ => "value") };
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("container-script", deployment, Execution: execution)], [new("MESSAGE")], deployment)));
        AutomationRules.Validate(new([new("container-script", deployment, Execution: execution)], [new("VALUE_0")], deployment));
    }

    [Theory]
    [InlineData("once")][InlineData("all")]
    public void ScriptTargetingAndEditorRoundtripPreserveWorkflowAndMetadata(string replicas)
    {
        var deployment = Guid.NewGuid();
        var plan = new ActionPlan([new("deploy", deployment), new("container-script", deployment, Execution: Script("echo ready\r\n") with { Replicas = replicas })],
            [new("BRANCH", Input: "branch", Source: "sources/app")], deployment, "QA", 42, "Proceed?", false);
        AutomationRules.Validate(plan);
        var model = ActionEditorModel.From(new() { Concurrency = "reject" }, plan);
        var saved = model.ToPlan(plan);
        Assert.Equal(JsonSerializer.Serialize(plan, AutomationRules.Json), JsonSerializer.Serialize(saved, AutomationRules.Json));
        Assert.Equal("reject", model.Concurrency);
        foreach (var invalid in new[] { Script() with { Services = [] }, Script() with { Services = ["app", "app"] },
            Script() with { Services = ["app;evil"] }, Script() with { Service = "app" }, Script() with { Replicas = "random" } })
            Assert.Throws<AutomationException>(() => AutomationRules.ValidateScript("container-script", invalid));
    }

    [Fact]
    public async Task BranchAndScriptWorkflowPinsSourcesAndRemembersEachActionsOwnVariables()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync();
        var commit = new string('a', 40);
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", "[\"main\",\"feature/qa\"]"),
            "resolve-action-inputs" => new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = commit }),
            _ => throw new InvalidOperationException()
        });
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            var plan = new ActionPlan([new("deploy", h.Deployment), new("container-script", h.Deployment, Execution: Script())],
                [new("BRANCH", Input: "branch", Source: "sources/app"), new("MESSAGE", Default: "default")], h.Deployment);
            var first = await automation.SaveActionAsync(h.Root, null, 0, "First", true, "queue", plan);
            var second = await automation.SaveActionAsync(h.Root, null, 0, "Second", true, "queue", plan);
            await automation.QueueActionAsync(h.Root, first.Id, new Dictionary<string, string> { ["BRANCH"] = "feature/qa", ["MESSAGE"] = "first" });
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await automation.QueueActionAsync(h.Root, second.Id, new Dictionary<string, string> { ["BRANCH"] = "main", ["MESSAGE"] = "second" });
            Assert.Equal("feature/qa", (await automation.InvocationAsync(h.Root, first.Id, default)).Values["BRANCH"]);
            Assert.Equal("first", (await automation.InvocationAsync(h.Root, first.Id, default)).Values["MESSAGE"]);
            Assert.Equal("main", (await automation.InvocationAsync(h.Root, second.Id, default)).Values["BRANCH"]);
            var operations = new FakeOperations();
            await new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), automation, s.GetRequiredService<AutomationAuthorization>(), operations,
                new UnavailableImageDigestProbe(), h.Clock).ExecuteNextAsync();
            Assert.Equal(2, operations.Submissions.Count);
            Assert.Equal(commit, operations.Submissions[0].SourceOverrides!["sources/app"]);
            Assert.Empty(operations.Submissions[1].SourceOverrides!);
            Assert.Equal("feature/qa", operations.Submissions[1].Execution!.Environment!["BRANCH"]);
            Assert.Equal("first", operations.Submissions[1].Execution!.Environment!["MESSAGE"]);
            Assert.Equal("refs/heads/feature/qa", AutomationRules.Bindings(plan, h.Deployment, "deploy", new Dictionary<string, string> { ["BRANCH"] = "feature/qa" }).Sources["sources/app"]);
            return true;
        });
    }

    [Fact]
    public async Task OperationGuardRejectsTamperedReplicaTargetsAndEnvironment()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            var plan = new ActionPlan([new("container-script", h.Deployment, Execution: Script())], [new("MESSAGE")], h.Deployment);
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Run", true, "queue", plan);
            var run = await automation.QueueActionAsync(h.Root, action.Id, new Dictionary<string, string> { ["MESSAGE"] = "literal" });
            var executor = new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), automation, s.GetRequiredService<AutomationAuthorization>(), new FakeOperations(), new UnavailableImageDigestProbe(), h.Clock);
            Assert.True(await executor.ClaimAsync(run));
            var execution = AutomationRules.Resolve(plan.Steps[0], new Dictionary<string, string> { ["MESSAGE"] = "literal" }).Execution!;
            var service = s.GetRequiredService<OperationService>();
            foreach (var invalid in new[] { execution with { Services = ["database"] }, execution with { Replicas = "all" },
                execution with { Environment = new Dictionary<string, string> { ["MESSAGE"] = "different" } } })
                await Assert.ThrowsAsync<OperationException>(() => service.EnqueueAsync(new(h.Root, h.Deployment, "container-script", "automation:" + run.Id.ToString("N"), Execution: invalid)));
            await service.EnqueueAsync(new(h.Root, h.Deployment, "container-script", "automation:" + run.Id.ToString("N"), Execution: execution));
            return true;
        });
    }
}
