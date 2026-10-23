using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.Core.Automation;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class ActionInputTests
{
    [Theory]
    [InlineData("busybox", "registry-1.docker.io", "library/busybox")]
    [InlineData("docker.io/org/app", "registry-1.docker.io", "org/app")]
    [InlineData("ghcr.io/org/app", "ghcr.io", "org/app")]
    [InlineData("example.azurecr.io/team/app", "example.azurecr.io", "team/app")]
    [InlineData("registry.test:5443/app", "registry.test:5443", "app")]
    public void RepositoryBindingsNormalizeDockerHubAndPreserveRegistryPorts(string value, string host, string path) =>
        Assert.Equal((host, path), ActionInputRules.Repository(value));

    [Theory]
    [InlineData("https://registry.test/app")][InlineData("busybox:latest")][InlineData("org/app@sha256:123")]
    [InlineData("registry.test/../escape")][InlineData("registry.test/App")][InlineData("registry.test/app?query")]
    public void RepositoryBindingsRejectTagsDigestsUrlsAndTraversal(string value) => Assert.Throws<ArgumentException>(() => ActionInputRules.Repository(value));

    [Theory]
    [InlineData("main", true)][InlineData("feature/QA-123", true)][InlineData("-branch", false)]
    [InlineData("refs/../secret", false)][InlineData("branch.lock", false)][InlineData("@{bad}", false)]
    [InlineData("branch name", false)][InlineData("branch\\name", false)]
    public void BranchNamesFollowGitReferenceSyntax(string value, bool expected) => Assert.Equal(expected, ActionInputRules.IsBranch(value));

    [Fact]
    public async Task LatestSubmittedValuesAreSharedEncryptedAndNotOverwrittenByRejectedRuns()
    {
        await using var s = await AutomationTestStore.Create();
        var plan = new ActionPlan([new("container-exec", s.Deployment, Execution: new("/usr/bin/reset-db", ["${database}"], Service: "app"))],
            [new("database", Input: "dropdown", Options: ["testing", "demo"], Default: "testing")], s.Deployment, "Reset DB", Confirmation: "Reset the database?");
        var action = await s.Action(plan); await s.Grant("action.execute", "action", action.Id);
        var run = await s.Service.QueueActionAsync(s.User, action.Id, new Dictionary<string, string> { ["database"] = "demo" });
        Assert.Equal("demo", (await s.Service.InvocationAsync(s.Root, action.Id, default)).Values["database"]);
        Assert.Equal("demo", (await s.Service.InvocationAsync(s.User, action.Id, default)).Values["database"]);
        var memory = await s.Db.Set<ActionInputMemory>().AsNoTracking().SingleAsync(); Assert.DoesNotContain("demo", memory.ProtectedValues);
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.User, action.Id, new Dictionary<string, string> { ["database"] = "production" }));
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.User, action.Id, expectedRevision: action.Revision + 1));
        Assert.Equal("demo", (await s.Service.InvocationAsync(s.User, action.Id, default)).Values["database"]);
        Assert.Single(await s.Db.Set<AutomationRun>().ToListAsync());
        await s.Executor().ExecuteNextAsync();
        var operation = Assert.Single(s.Operations.Submissions);
        Assert.Equal(s.Root, operation.ActorId); Assert.Equal("demo", operation.Execution!.Arguments[0]);
        Assert.Equal(s.User, (await s.Service.ReadRunAsync(s.User, run.Id)).Run.RequestedBy);
        Assert.False(await s.Access.IsAllowedAsync(s.User, "container.execute", "deployment", s.Deployment));
        await s.Service.SaveLimitsAsync(s.Root, new(MaximumQueuedRuns: 1));
        await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["database"] = "demo" });
        await Assert.ThrowsAsync<AutomationException>(() => s.Service.QueueActionAsync(s.User, action.Id, new Dictionary<string, string> { ["database"] = "testing" }));
        Assert.Equal("demo", (await s.Service.InvocationAsync(s.User, action.Id, default)).Values["database"]);
    }

    [Fact]
    public async Task DelegatedGrantCannotChangeCommandsTargetsOrBoundInputs()
    {
        await using var s = await AutomationTestStore.Create();
        var command = new AgentExecution("/usr/bin/reset-db", ["--testing"], Service: "app");
        var action = await s.Action(new([new("container-exec", s.Deployment, Execution: command)]));
        await s.Grant("action.execute", "action", action.Id);
        var run = await s.Service.QueueActionAsync(s.User, action.Id); await s.Executor().ClaimAsync(run);
        var guard = new AutomationOperationGuard(s.Db, s.Service, s.Authorization);
        var submission = new Stanza.ControlPlane.Operations.OperationSubmission(s.Root, s.Deployment, "container-exec", "automation:" + run.Id.ToString("N"), Execution: command);
        await guard.ValidateSubmissionAsync(submission, default);
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.ValidateSubmissionAsync(submission with { ActorId = s.User }, default));
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.ValidateSubmissionAsync(submission with { DeploymentId = s.OtherDeployment }, default));
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.ValidateSubmissionAsync(submission with { Execution = command with { Arguments = ["--production"] } }, default));
        await Assert.ThrowsAsync<AutomationAccessException>(() => guard.ValidateSubmissionAsync(submission with { ImageOverrides = new Dictionary<string, string> { ["app"] = "evil:latest" } }, default));
    }

    [Fact]
    public async Task CompatibleEditsKeepSharedSelectionsButChangedDropdownDefinitionsDiscardIncompatibleValues()
    {
        await using var s = await AutomationTestStore.Create();
        var plan = new ActionPlan([new("restart", s.Deployment)], [new("mode", Input: "dropdown", Options: ["one", "two"], Default: "one")], s.Deployment);
        var action = await s.Action(plan); await s.Service.QueueActionAsync(s.Root, action.Id, new Dictionary<string, string> { ["mode"] = "two" });
        action = await s.Service.SaveActionAsync(s.Root, action.Id, action.Revision, "Renamed", true, "queue", plan with { ButtonLabel = "Restart" });
        Assert.Equal("two", (await s.Service.InvocationAsync(s.Root, action.Id, default)).Values["mode"]);
        await s.Service.SaveActionAsync(s.Root, action.Id, action.Revision, "Changed", true, "queue", plan with {
            Parameters = [new("mode", Input: "dropdown", Options: ["one", "three"], Default: "one")] });
        Assert.Equal("one", (await s.Service.InvocationAsync(s.Root, action.Id, default)).Values["mode"]);
    }

    [Fact]
    public void BoundSelectionsApplyOnlyToTheirDeploymentSnapshotAndNotRestartOrExec()
    {
        var deployment = Guid.NewGuid();
        var plan = new ActionPlan([new("deploy", deployment)], [new("branch", Input: "branch", Source: "src"),
            new("version", Input: "version", Repository: "ghcr.io/org/app", Services: ["web", "worker"])], deployment);
        AutomationRules.Validate(plan);
        var values = AutomationRules.Parameters(plan, new Dictionary<string, string> { ["branch"] = "feature/qa", ["version"] = "v1.2.3" });
        var bindings = AutomationRules.Bindings(plan, deployment, "deploy", values);
        Assert.Equal("refs/heads/feature/qa", bindings.Sources["src"]); Assert.Equal("ghcr.io/org/app:v1.2.3", bindings.Images["web"]);
        Assert.Empty(AutomationRules.Bindings(plan, Guid.NewGuid(), "deploy", values).Sources);
        Assert.Empty(AutomationRules.Bindings(plan, deployment, "restart", values).Images);
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { DeploymentId = null }));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { Parameters = [new("v", Input: "version", Repository: "app:tag", Services: ["app"])] }));
        Assert.Throws<AutomationException>(() => AutomationRules.Validate(plan with { Parameters = [new("v", Input: "dropdown", Options: ["same", "same"])] }));
        Assert.Throws<AutomationException>(() => AutomationRules.Parameters(plan, new Dictionary<string, string> { ["branch"] = "main", ["version"] = "bad tag" }));
    }
}
