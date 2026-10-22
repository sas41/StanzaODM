using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class ActionTests
{
    [Fact]
    public async Task CrossTargetStepsUseOrderedImmutableSnapshotsAndTypedWholeArguments()
    {
        await using var s = await AutomationTestStore.Create();
        var host = Guid.NewGuid();
        var action = await s.Action(new([new("build", s.Deployment), new("container-exec", s.OtherDeployment,
            Execution: new AgentExecution("/usr/bin/printf", ["%s", "${message}"], Service: "app", User: "1000", WorkingDirectory: "/app", Environment: new Dictionary<string, string> { ["MODE"] = "safe" })),
            new("host-exec", AgentId: host, Execution: new("/usr/bin/true", []))], [new("message")]));
        const string attack = "; $(touch /tmp/nope) ' \" --flag";
        var run = await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["message"] = attack });
        await s.Service.SaveActionAsync(s.Root, action.Id, action.Revision, "Edited", true, "queue", new([new("remove", s.OtherDeployment)]));
        Assert.True(await s.Executor().ExecuteNextAsync());
        Assert.Equal(new[] { "build", "container-exec", "host-exec" }, s.Operations.Submissions.Select(x => x.Kind));
        Assert.Equal(s.OtherDeployment, s.Operations.Submissions[1].DeploymentId);
        Assert.Equal(attack, s.Operations.Submissions[1].Execution!.Arguments[1]);
        Assert.Null(s.Operations.Submissions[2].DeploymentId); Assert.Equal(host, s.Operations.Submissions[2].AgentId);
        var details = await s.Service.ReadRunAsync(s.Root, run.Id);
        Assert.Equal("succeeded", details.Run.State); Assert.Equal(3, details.Steps.Count);
        Assert.All(details.Steps, x => { Assert.Equal("succeeded", x.State); Assert.NotNull(x.OperationId); Assert.NotNull(x.AgentId); });
        var stored = await s.Db.Set<AutomationRun>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain(attack, stored.ProtectedParameters); Assert.DoesNotContain("printf", stored.ProtectedPlan);
    }

    [Fact]
    public async Task ActionGrantDoesNotAuthorizeCrossDeploymentOrHostTargets()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("deploy", s.Deployment), new("deploy", s.OtherDeployment)]));
        await s.Grant("action.execute", "action", action.Id); await s.Grant("deployment.deploy", "deployment", s.Deployment);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.QueueActionAsync(s.User, action.Id));
        await s.Grant("deployment.deploy", "deployment", s.OtherDeployment);
        await s.Service.QueueActionAsync(s.User, action.Id);
        await s.Executor().ExecuteNextAsync(); Assert.Equal(2, s.Operations.Submissions.Count);
        var host = await s.Action(new([new("host-exec", AgentId: Guid.NewGuid(), Execution: new("true", []))]));
        await s.Grant("action.execute", "action", host.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.QueueActionAsync(s.User, host.Id));
    }

    [Fact]
    public async Task HostEditingIsSeparatelyPrivilegedAndOwnershipChangesOnSave()
    {
        await using var s = await AutomationTestStore.Create(); var host = Guid.NewGuid();
        var plan = new ActionPlan([new("host-exec", AgentId: host, Execution: new("true", []))]);
        var action = await s.Action(plan); await s.Grant("action.manage", "action", action.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.SaveActionAsync(s.User, action.Id, 1, "Host", true, "queue", plan));
        await s.Grant("host.execute", "agent", host);
        var edited = await s.Service.SaveActionAsync(s.User, action.Id, 1, "Host", true, "queue", plan);
        Assert.Equal(s.User, edited.OwnerId);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.SaveActionAsync(s.User, action.Id, 1, "Stale", true, "queue", plan));
    }

    [Fact]
    public async Task RevokedQueuedOwnerDoesNotExecuteAndDisabledActionStopsQueuedWork()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        await s.Grant("action.execute", "action", action.Id); var grant = await s.Grant("deployment.deploy", "deployment", s.Deployment);
        var run = await s.Service.QueueActionAsync(s.User, action.Id);
        await s.Access.RemoveGrantAsync(s.Root, grant.Id); await s.Executor().ExecuteNextAsync();
        Assert.Empty(s.Operations.Submissions); Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        var queued = await s.Service.QueueActionAsync(s.Root, action.Id);
        await s.Service.SaveActionAsync(s.Root, action.Id, 1, "Paused", false, "queue", new([new("deploy", s.Deployment)]));
        await s.Executor().ExecuteNextAsync(); Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, queued.Id)).Run.State);
    }

    [Theory]
    [InlineData("stop", 1)][InlineData("continue", 2)]
    public async Task FailurePolicyControlsRemainingSteps(string failure, int count)
    {
        await using var s = await AutomationTestStore.Create(); s.Operations.State = "Failed";
        var action = await s.Action(new([new("deploy", s.Deployment, Failure: failure), new("start", s.OtherDeployment)]));
        var run = await s.Service.QueueActionAsync(s.Root, action.Id); await s.Executor().ExecuteNextAsync();
        Assert.Equal(count, s.Operations.Submissions.Count); Assert.Equal("failed", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotDispatchAndUnknownOutcomeIsNotReplayed()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var cancelled = await s.Service.QueueActionAsync(s.Root, action.Id); await s.Service.CancelRunAsync(s.Root, cancelled.Id);
        await s.Executor().ExecuteNextAsync(); Assert.Empty(s.Operations.Submissions);
        Assert.Equal("cancelled", (await s.Service.ReadRunAsync(s.Root, cancelled.Id)).Run.State);
        s.Operations.State = "Unknown"; var uncertain = await s.Service.QueueActionAsync(s.Root, action.Id);
        await s.Executor().ExecuteNextAsync(); Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, uncertain.Id)).Run.State);
        var next = await s.Service.QueueActionAsync(s.Root, action.Id); Assert.False(await s.Executor().ExecuteNextAsync());
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.ReconcileRunAsync(s.Root, uncertain.Id, false));
        await s.Service.ReconcileRunAsync(s.Root, uncertain.Id, true);
        Assert.Equal(s.Root, (await s.Service.ReadRunAsync(s.Root, uncertain.Id)).Run.ReconciledBy);
        s.Operations.State = "Succeeded"; Assert.True(await s.Executor().ExecuteNextAsync());
        Assert.Equal("succeeded", (await s.Service.ReadRunAsync(s.Root, next.Id)).Run.State);
    }

    [Theory]
    [InlineData("queue", "queued")][InlineData("reject", "skipped")]
    public async Task DurableActionConcurrencyClaimsNeverDispatchTwice(string policy, string expected)
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action(concurrency: policy);
        var first = await s.Service.QueueActionAsync(s.Root, action.Id); var second = await s.Service.QueueActionAsync(s.Root, action.Id);
        Assert.True(await s.Executor().ClaimAsync(first)); Assert.False(await s.Executor().ClaimAsync(first));
        Assert.False(await s.Executor().ClaimAsync(second)); Assert.Equal(expected, (await s.Service.ReadRunAsync(s.Root, second.Id)).Run.State);
    }

    [Fact]
    public async Task EnqueueGapIsUncertainAndNeverLogsExceptionContents()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var run = await s.Service.QueueActionAsync(s.Root, action.Id); s.Operations.ThrowAfterEnqueue = true;
        await s.Executor().ExecuteNextAsync(); var detail = await s.Service.ReadRunAsync(s.Root, run.Id);
        Assert.Equal("uncertain", detail.Run.State); Assert.DoesNotContain("secret", detail.Run.Code);
        Assert.False(await s.Executor().ExecuteNextAsync()); Assert.Single(s.Operations.Submissions);
    }

    [Fact]
    public async Task StepTimeoutRequestsCancellationAndRetainsUncertainLock()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("deploy", s.Deployment, TimeoutSeconds: 1)]));
        var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        s.Operations.OnWait = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        await s.Executor().ExecuteNextAsync();
        Assert.Single(s.Operations.Cancelled); Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        Assert.Equal("step-timeout-cancellation-requested", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.Code);
        Assert.Equal(run.Id, (await s.Db.Set<ActionDefinition>().AsNoTracking().SingleAsync()).ActiveRunId);
    }

    [Fact]
    public async Task RunningCancellationRequestsOperationCancellationWithoutClaimingRollback()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var run = await s.Service.QueueActionAsync(s.Root, action.Id);
        s.Operations.OnWait = async token => { await s.Service.CancelRunAsync(s.Root, run.Id); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        await s.Executor().ExecuteNextAsync(); Assert.Single(s.Operations.Cancelled);
        Assert.Equal("uncertain", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Fact]
    public async Task DispatchGuardChecksActionExecuteAfterOperationWasQueued()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action();
        var execute = await s.Grant("action.execute", "action", action.Id); await s.Grant("deployment.deploy", "deployment", s.Deployment);
        var run = await s.Service.QueueActionAsync(s.User, action.Id); Assert.True(await s.Executor().ClaimAsync(run));
        var guard = new AutomationOperationGuard(s.Db, s.Service, s.Authorization); var trigger = "automation:" + run.Id.ToString("N");
        await guard.AuthorizeAsync(s.User, trigger, "deploy", s.Deployment, Guid.NewGuid());
        await s.Access.RemoveGrantAsync(s.Root, execute.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.AuthorizeAsync(s.User, trigger, "deploy", s.Deployment, Guid.NewGuid()));
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.AuthorizeAsync(s.Root, trigger, "deploy", s.Deployment, Guid.NewGuid()));
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.AuthorizeAsync(s.User, "automation:malformed", "deploy", s.Deployment, Guid.NewGuid()));
    }

    [Fact]
    public async Task CentralAuditContainsTriggersTargetResolutionsAndResultsButNoCommandOrParameterSecrets()
    {
        await using var s = await AutomationTestStore.Create();
        var action = await s.Action(new([new("container-exec", s.Deployment, Execution: new("printf", ["%s", "${message}"], Service: "app",
            Environment: new Dictionary<string, string> { ["TOKEN"] = "secret-environment-value" }))], [new("message")]));
        var run = await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["message"] = "secret-parameter-value" });
        await s.Executor().ExecuteNextAsync();
        var events = await s.Db.Set<Stanza.ControlPlane.Audit.AuditEvent>().AsNoTracking().ToListAsync();
        Assert.Contains(events, x => x.Action == "automation.action.saved");
        Assert.Contains(events, x => x.Action == "automation.trigger.manual" && x.Resource == run.Id.ToString());
        Assert.Contains(events, x => x.Action == "automation.target.resolved"); Assert.Contains(events, x => x.Action == "automation.completed");
        var json = System.Text.Json.JsonSerializer.Serialize(events);
        Assert.DoesNotContain("secret-environment-value", json); Assert.DoesNotContain("secret-parameter-value", json); Assert.DoesNotContain("printf", json);
    }

    [Fact]
    public async Task GitRefreshUsesNormalSourcePreparationAndValidationRatherThanAdHocShell()
    {
        await using var s = await AutomationTestStore.Create(); var action = await s.Action(new([new("git-refresh", s.Deployment)]));
        await s.Grant("action.execute", "action", action.Id); await s.Grant("deployment.validate", "deployment", s.Deployment);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.QueueActionAsync(s.User, action.Id));
        await s.Grant("sources.manage", "deployment", s.Deployment);
        var run = await s.Service.QueueActionAsync(s.User, action.Id); await s.Executor().ExecuteNextAsync();
        var operation = Assert.Single(s.Operations.Submissions); Assert.Equal("validate", operation.Kind); Assert.Null(operation.Execution);
        Assert.Equal("git-refresh", Assert.Single((await s.Service.ReadRunAsync(s.Root, run.Id)).Steps).Kind);
    }

    [Theory]
    [InlineData("sh", "${value}")][InlineData("/bin/bash", "${value}")][InlineData("/usr/bin/printf", "prefix-${value}")]
    [InlineData("/usr/bin/printf", "${unknown}")]
    public void ShellAndPartialSubstitutionAreRejected(string executable, string argument) => Assert.Throws<AutomationException>(() =>
        AutomationRules.Validate(new([new("container-exec", Guid.NewGuid(), Execution: new(executable, [argument], Service: "app"))], [new("value")])));

    [Fact]
    public void ExecRequiresExplicitTargetAndCannotParameterizeExecutableOrEnvironment()
    {
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("host-exec", Guid.NewGuid(), Execution: new("true", []))])));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("container-exec", Guid.NewGuid(), Execution: new("true", []))])));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("container-exec", Guid.NewGuid(), Execution: new("${exe}", [], Service: "app"))])));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(new([new("container-exec", Guid.NewGuid(), Execution: new("true", [], Service: "app", Environment: new Dictionary<string, string> { ["X"] = "${x}" }))])));
    }

    [Fact]
    public void TypedParametersRejectUnknownOversizedInvalidTypesAndMissingValues()
    {
        var plan = new ActionPlan([new("deploy", Guid.NewGuid())], [new("number", "integer"), new("flag", "boolean", Default: "true")]);
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(plan, null));
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(plan, new Dictionary<string, string> { ["number"] = "$(id)" }));
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(plan, new Dictionary<string, string> { ["number"] = "1", ["flag"] = "1" }));
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(plan, new Dictionary<string, string> { ["number"] = "1", ["unknown"] = "1" }));
        Assert.Equal("true", AutomationRules.Parameters(plan, new Dictionary<string, string> { ["number"] = "-12" })["flag"]);
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(new([], [new("x")]), new Dictionary<string, string> { ["x"] = new('x', 4097) }));
    }
}
