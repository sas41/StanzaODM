using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;
using Stanza.Tests.Operations;

namespace Stanza.Tests.Automation;

public sealed class DiscoveryConcurrencyTests
{
    [Theory]
    [InlineData("agent_busy")][InlineData("discovery_timeout")]
    public async Task SourceBranchDiscoveryReportsCapacityContentionRatherThanBadCredentials(string code)
    {
        await using var h = await OperationTestHarness.CreateAsync(); var source = await h.AddSourceAsync();
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, false, code));
        var error = await Assert.ThrowsAsync<SourceException>(() => h.InScope(s => s.GetRequiredService<SourceService>().BranchesAsync(h.Root, h.Deployment, source.Id)));
        Assert.Contains("busy or timed out", error.Message); Assert.DoesNotContain("credentials", error.Message);
    }

    [Fact]
    public async Task SlowDiscoveryForOneScheduleDoesNotHoldUpOtherSchedules()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync();
        var other = await h.AddDeploymentAsync("independent", h.Agent);
        var secondSchedule = await h.InScope(async s =>
        {
            await s.GetRequiredService<SourceService>().SaveGitAsync(h.Root, other, null, 0, new("https://git.example.test/app", "main", "sources/app", null));
            var automation = s.GetRequiredService<AutomationService>(); Guid second = Guid.Empty;
            foreach (var target in new[] { h.Deployment, other })
            {
                var action = await automation.SaveActionAsync(h.Root, null, 0, target.ToString(), true, "queue", new([new("deploy", target)],
                    [new("BRANCH", Default: "main", Input: "branch", Source: "sources/app")], target));
                var schedule = await automation.SaveActionScheduleAsync(h.Root, action.Id, Guid.Empty, 0, target.ToString(), "* * * * *", "UTC", true, "queue", "skip");
                if (target == other) second = schedule.Id;
            }
            return second;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, ct) =>
        {
            if (request.DeploymentId == h.Deployment && request.Operation == "git-action-branches")
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return request.Operation == "resolve-action-inputs"
                ? new(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = new('a', 40) })
                : new(request.Id, true, "ok", "[\"main\"]");
        };
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var tick = h.InScope(async s => { await s.GetRequiredService<AutomationScheduler>().TickAsync(); return true; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await OperationTestHarness.UntilAsync(() => h.InScope(s => s.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().AnyAsync(x => x.DefinitionId == secondSchedule && x.State == "queued")));
            Assert.False(tick.IsCompleted);
        }
        finally { release.TrySetResult(); await tick; }
    }

    [Theory]
    [InlineData("branches")][InlineData("credential")][InlineData("schedule")][InlineData("webhook")]
    public async Task RemoteDiscoveryNeverHoldsTheGlobalDatabaseWriteLock(string path)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var credential = await h.GitCredentialAsync(); var source = await h.AddSourceAsync(credential.Id);
        var action = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue",
            new([new("deploy", h.Deployment)], [new("BRANCH", Default: "main", Input: "branch", Source: "sources/app")], h.Deployment)));
        var schedule = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveActionScheduleAsync(h.Root, action.Id, Guid.Empty, 0, "Scheduled", "* * * * *", "UTC", true, "queue", "skip"));
        var hook = await h.InScope(s => s.GetRequiredService<AutomationService>().SaveWebhookAsync(h.Root, new() { Name = "Hook", ActionId = action.Id, DeploymentId = h.Deployment, Enabled = true }, 0));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, ct) =>
        {
            if (request.Operation == "resolve-action-inputs") return new(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = new('a', 40) });
            Assert.Contains(request.Operation, new[] { "git-branches", "git-action-branches" });
            if (path is "schedule" or "webhook") Assert.False(request.IncludeGitMetadata);
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return new(request.Id, true, "ok", "[\"main\"]");
        };
        var body = Encoding.UTF8.GetBytes("{}");
        var discovery = h.InScope<object>(async s =>
        {
            if (path == "branches") return await s.GetRequiredService<SourceService>().BranchesAsync(h.Root, h.Deployment, source.Id);
            if (path == "credential") { await s.GetRequiredService<SourceService>().TestCredentialAsync(h.Root, credential.Id, h.Agent); return true; }
            if (path == "schedule") return await s.GetRequiredService<AutomationScheduler>().ClaimScheduleAsync(schedule);
            return await new WebhookReceiver(s.GetRequiredService<StanzaDbContext>(), s.GetRequiredService<AutomationService>(), s.GetRequiredService<AutomationAuthorization>())
                .ReceiveAsync(hook.Definition.Id, WebhookTests.Sign(hook.Secret!, body), "delivery", "push", body);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await h.InScope(async s =>
            {
                var policies = s.GetRequiredService<OperationalPolicyService>(); var current = await policies.GetAsync();
                await policies.SaveAsync(h.Root, current with { Policy = current.Policy with { OperationTimeoutSeconds = 21 } }); return true;
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); }
        var result = await discovery.WaitAsync(TimeSpan.FromSeconds(5));
        if (result is WebhookReceipt receipt) Assert.Equal(202, receipt.Status);
        if (path == "schedule") Assert.Equal(true, result);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task TemporaryDiscoveryContentionDoesNotConsumeAScheduledOccurrenceOrWebhookDelivery(bool webhook)
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AddSourceAsync(); var busy = true;
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(busy ? new AgentWorkResponse(request.Id, false, "agent_busy") : request.Operation switch
        {
            "git-action-branches" => new(request.Id, true, "ok", "[\"main\"]"),
            "resolve-action-inputs" => new(request.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { ["sources/app"] = new('a', 40) }),
            _ => throw new InvalidOperationException()
        });
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>(); var db = s.GetRequiredService<StanzaDbContext>();
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Deploy", true, "queue", new([new("deploy", h.Deployment)],
                [new("BRANCH", Default: "main", Input: "branch", Source: "sources/app")], h.Deployment));
            if (webhook)
            {
                var saved = await automation.SaveWebhookAsync(h.Root, new() { Name = "Hook", ActionId = action.Id, DeploymentId = h.Deployment, Enabled = true }, 0);
                var receiver = new WebhookReceiver(db, automation, s.GetRequiredService<AutomationAuthorization>()); var body = Encoding.UTF8.GetBytes("{}");
                var signature = WebhookTests.Sign(saved.Secret!, body);
                Assert.Equal(503, (await receiver.ReceiveAsync(saved.Definition.Id, signature, "retryable", "push", body)).Status);
                Assert.False(await db.Set<WebhookDelivery>().AnyAsync()); Assert.False(await db.Set<AutomationRun>().AnyAsync());
                busy = false;
                Assert.Equal(202, (await receiver.ReceiveAsync(saved.Definition.Id, signature, "retryable", "push", body)).Status);
                Assert.Single(await db.Set<WebhookDelivery>().ToListAsync());
            }
            else
            {
                var schedule = await automation.SaveActionScheduleAsync(h.Root, action.Id, Guid.Empty, 0, "Scheduled", "* * * * *", "UTC", true, "queue", "skip");
                h.Clock.Advance(TimeSpan.FromMinutes(1));
                var scheduler = s.GetRequiredService<AutomationScheduler>();
                Assert.False(await scheduler.ClaimScheduleAsync(schedule)); Assert.False(await db.Set<AutomationRun>().AnyAsync());
                var retry = await db.Set<AutomationSchedule>().AsNoTracking().SingleAsync();
                Assert.Equal(schedule.NextRunAt, retry.PendingOccurrenceAt);
                // Even a long contention delay must not turn a previously attempted occurrence into a missed run.
                h.Clock.Advance(TimeSpan.FromMinutes(2));
                busy = false; Assert.True(await scheduler.ClaimScheduleAsync(retry));
                Assert.Equal($"schedule:{schedule.Id:N}:{schedule.NextRunAt}", (await db.Set<AutomationRun>().AsNoTracking().SingleAsync()).OccurrenceKey);
                Assert.Null((await db.Set<AutomationSchedule>().AsNoTracking().SingleAsync()).PendingOccurrenceAt);
            }
            Assert.Equal("queued", (await db.Set<AutomationRun>().AsNoTracking().SingleAsync()).State);
            return true;
        });
    }
}
