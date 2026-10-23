using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Automation;

public sealed class DeploymentActionIntegrationTests
{
    [Fact]
    public async Task CronReResolvesLastUsedBranchAtEachOccurrenceAndUnavailableReferencesDoNotGetRetried()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync();
        var commit = new string('a', 40); var available = true;
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", JsonSerializer.Serialize(available ? new[] { "main" } : [])),
            "resolve-action-inputs" => new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = commit }),
            _ => throw new InvalidOperationException("Unexpected work")
        });
        await h.InScope(async services =>
        {
            var automation = services.GetRequiredService<AutomationService>(); var db = services.GetRequiredService<StanzaDbContext>();
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Branch deploy", true, "queue", new([new("deploy", h.Deployment)],
                [new("branch", Input: "branch", Source: "sources/app")], h.Deployment));
            await automation.QueueActionAsync(h.Root, action.Id, new Dictionary<string, string> { ["branch"] = "main" });
            await automation.SaveActionScheduleAsync(h.Root, action.Id, Guid.Empty, 0, "Deploy latest branch", "* * * * *", "UTC", true, "queue", "skip");
            var scheduler = new AutomationScheduler(db, automation, services.GetRequiredService<AutomationAuthorization>(), h.Clock);
            h.Clock.Advance(TimeSpan.FromMinutes(1)); await scheduler.TickAsync();
            var first = await db.Set<AutomationRun>().AsNoTracking().SingleAsync(x => x.Source == "schedule");
            Assert.Equal(commit, automation.Unprotect<ActionExecutionBindings>(first.ProtectedBindings).Sources["sources/app"]);
            commit = new string('b', 40); h.Clock.Advance(TimeSpan.FromMinutes(1)); await scheduler.TickAsync();
            var next = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").OrderByDescending(x => x.CreatedAt).FirstAsync();
            Assert.Equal(commit, automation.Unprotect<ActionExecutionBindings>(next.ProtectedBindings).Sources["sources/app"]);
            Assert.Equal(new string('a', 40), automation.Unprotect<ActionExecutionBindings>(first.ProtectedBindings).Sources["sources/app"]);
            available = false; h.Clock.Advance(TimeSpan.FromMinutes(1)); await scheduler.TickAsync(); await scheduler.TickAsync();
            var denied = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.Source == "schedule").OrderByDescending(x => x.CreatedAt).FirstAsync();
            Assert.Equal("denied", denied.State); Assert.Equal("queue-or-inputs-unavailable", denied.Code);
            Assert.Equal(3, await db.Set<AutomationRun>().CountAsync(x => x.Source == "schedule"));
            return true;
        });
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task QaCanQueueFixedDatabaseResetWithoutCommandAccessAndRevocationStopsDispatch(bool revoke)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var command = new AgentExecution("/usr/bin/reset-db", ["--testing"], Service: "app");
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Reset DB", true, "queue",
            new([new("container-exec", h.Deployment, Execution: command)], DeploymentId: h.Deployment)));
        var grant = await h.GrantAsync("action.execute", "action", action.Id);
        var queued = await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            var run = await automation.QueueActionAsync(h.User, action.Id);
            var executor = new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), automation, s.GetRequiredService<AutomationAuthorization>(), new FakeOperations(), new UnavailableImageDigestProbe(), h.Clock);
            Assert.True(await executor.ClaimAsync(run));
            return await s.GetRequiredService<OperationService>().EnqueueAsync(new(h.Root, h.Deployment, "container-exec", "automation:" + run.Id.ToString("N"), Execution: command));
        });
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync("container-exec", h.User, execution: command));
        if (revoke) await h.RevokeAsync(grant.Id);
        await h.StartWorkerAsync();
        var completed = await h.WaitStateAsync(queued.Id, revoke ? "Failed" : "Succeeded");
        Assert.Equal(h.Root, completed.ActorId);
        if (revoke) Assert.Empty(h.Dispatcher.Requests);
        else
        {
            var request = Assert.Single(h.Dispatcher.Requests).Request;
            Assert.Equal(command.Executable, request.Execution!.Executable); Assert.Equal(command.Arguments, request.Execution.Arguments);
            Assert.Empty(request.Files); Assert.Empty(request.EnvironmentFiles);
        }
    }

    [Fact]
    public async Task BranchAndTagChoicesUseOwnerCredentialsAndFreezeSelectionsAcrossEveryStep()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var gitCredential = await h.GitCredentialAsync(); await h.AddSourceAsync(gitCredential.Id);
        var registry = await h.RegistryCredentialAsync();
        await h.InScope(async s => { await s.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [registry.Id], []); return true; });
        var commit = new string('a', 40); var digest = "sha256:" + new string('b', 64);
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", JsonSerializer.Serialize(new[] { "main", "feature/qa" })),
            "registry-tags" => new(request.Id, true, "ok", JsonSerializer.Serialize(Enumerable.Range(0, 75).Select(i => "v" + i).ToArray())),
            "resolve-action-inputs" => new AgentWorkResponse(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = commit },
                ResolvedImages: new Dictionary<string, string> { ["app"] = digest }),
            _ => throw new InvalidOperationException("Unexpected work")
        });
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue",
            new([new("build", h.Deployment), new("deploy", h.Deployment)], [new("branch", Input: "branch", Source: "sources/app"),
                new("version", Input: "version", Repository: "registry.example.test/app", Services: ["app"])], h.Deployment)));
        await h.GrantAsync("action.execute", "action", action.Id);
        await h.InScope(async s =>
        {
            var service = s.GetRequiredService<AutomationService>();
            var page = await service.ChoicesAsync(h.User, action.Id, "version", "", 0, false, default);
            Assert.Equal(50, page.Values.Count); Assert.True(page.HasMore);
            Assert.Equal(25, (await service.ChoicesAsync(h.User, action.Id, "version", "", 1, false, default)).Values.Count);
            Assert.Single((await service.ChoicesAsync(h.User, action.Id, "version", "v74", 0, false, default)).Values);
            await Assert.ThrowsAsync<AutomationException>(() => service.QueueActionAsync(h.User, action.Id,
                new Dictionary<string, string> { ["branch"] = "deleted", ["version"] = "v74" }));
            var run = await service.QueueActionAsync(h.User, action.Id, new Dictionary<string, string> { ["branch"] = "feature/qa", ["version"] = "v74" });
            var fake = new FakeOperations();
            var executor = new AutomationExecutor(s.GetRequiredService<StanzaDbContext>(), service, s.GetRequiredService<AutomationAuthorization>(), fake, new UnavailableImageDigestProbe(), h.Clock);
            await executor.ExecuteNextAsync();
            Assert.Equal(2, fake.Submissions.Count);
            Assert.All(fake.Submissions, op => { Assert.Equal(commit, op.SourceOverrides!["sources/app"]); Assert.Equal("registry.example.test/app@" + digest, op.ImageOverrides!["app"]); });
            Assert.Equal("v74", (await service.InvocationAsync(h.User, action.Id, default)).Values["version"]);
            Assert.Equal(h.User, (await service.ReadRunAsync(h.User, run.Id)).Run.RequestedBy);
            return true;
        });
        Assert.Contains(h.Dispatcher.Requests, r => r.Request.Sources?.SingleOrDefault()?.Password == "git-password-secret");
        Assert.Contains(h.Dispatcher.Requests, r => r.Request.Registries?.SingleOrDefault()?.Password == "registry-password-secret");
        var listingCount = h.Dispatcher.Requests.Count(x => x.Request.Operation == "registry-tags"); Assert.Equal(2, listingCount); // cached pages + fresh run validation
    }

    [Fact]
    public async Task OverviewGroupsByAgentAndShowsOnlyGrantedActionsWithoutGrantingDeploymentRead()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var otherAgent = await h.AddAgentAsync("secondary");
        var second = await h.AddDeploymentAsync("Staging", otherAgent);
        await h.InScope(async s =>
        {
            var service = s.GetRequiredService<AutomationService>();
            var reset = await service.SaveActionAsync(h.Root, null, 0, "Reset DB", true, "queue", new([new("restart", h.Deployment)], DeploymentId: h.Deployment));
            await service.SaveActionAsync(h.Root, null, 0, "Destroy", true, "queue", new([new("remove", h.Deployment)], DeploymentId: h.Deployment));
            await service.SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue", new([new("deploy", second)], DeploymentId: second));
            await s.GetRequiredService<AccessService>().AddGrantAsync(h.Root, h.User, null, "action.execute", "action", reset.Id);
            var overview = s.GetRequiredService<DeploymentActionOverview>();
            var qa = Assert.Single(await overview.ReadAsync(h.User, default));
            Assert.Equal(h.Agent, qa.Id); Assert.Equal("Reset DB", Assert.Single(Assert.Single(qa.Deployments).Actions).Label);
            Assert.Equal(2, (await overview.ReadAsync(h.Root, default)).Count);
            Assert.False(await s.GetRequiredService<AccessService>().IsAllowedAsync(h.User, "deployment.read", "deployment", h.Deployment));
            return true;
        });
    }
}
