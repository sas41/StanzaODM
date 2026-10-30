using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Operations;

public sealed class OperationsOverviewTests
{
    [Fact]
    public async Task QueuedActionsWithoutOperationsAreVisibleWithNamesAgentDeploymentAndRealTriggerIdentity()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>();
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Reset database", true, "queue", new([new("restart", h.Deployment)], DeploymentId: h.Deployment));
            var run = await automation.QueueActionAsync(h.Root, action.Id);
            var row = Assert.Single(await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.Root, null));
            Assert.Equal("Reset database", row.ActionName); Assert.Equal("primary", row.Agent); Assert.Equal("App", row.Deployment);
            Assert.Equal("root", row.Trigger); Assert.Equal(run.Id, row.Id); Assert.True(row.IsActionRun); Assert.Equal("—", row.Duration(100));
            Assert.Equal(run.Id, row.ActionRunId);
            Assert.Empty(await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.User, null));
            await s.GetRequiredService<Stanza.ControlPlane.Security.AccessService>().AddGrantAsync(h.Root, h.User, null, "action.execute", "action", action.Id);
            var delegated = Assert.Single(await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.User, null));
            Assert.Equal("Reset database", delegated.ActionName); Assert.Equal("—", delegated.Agent); Assert.Equal("—", delegated.Deployment);
            Assert.Empty(await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.Root, Guid.NewGuid()));
            return true;
        });
    }

    [Fact]
    public async Task ActiveOperationsAreNeverHiddenBehindRecentHistoryOrUnauthorizedRows()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.GrantAsync("deployment.read", "deployment", h.Deployment);
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            db.AddRange(Enumerable.Range(0, 550).Select(i => new OperationEntity { Id = Guid.NewGuid(), DeploymentId = Guid.NewGuid(), CreatedAt = 10000 + i, State = "Succeeded" }));
            db.AddRange(Enumerable.Range(0, 501).Select(i => new OperationEntity { Id = Guid.NewGuid(), ActorId = h.Root, AgentId = h.Agent, DeploymentId = h.Deployment,
                CreatedAt = i + 1, State = "Succeeded", Kind = "restart", StartedAt = 1000, CompletedAt = 1030 }));
            var queued = new OperationEntity { Id = Guid.NewGuid(), ActorId = h.Root, AgentId = h.Agent, DeploymentId = h.Deployment, State = "Queued", CreatedAt = 0, Kind = "deploy" };
            db.Add(queued); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var rows = await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.User, null);
            Assert.Equal(501, rows.Count); Assert.Equal(queued.Id, rows[0].Id);
            Assert.Equal(500, rows.Count(x => x.State == "Succeeded"));
            Assert.Equal("00:00:30", rows.First(x => x.State == "Succeeded").Duration(9999));
            Assert.All(rows, x => Assert.Equal("—", x.ActionName));
            return true;
        });
    }

    [Fact]
    public async Task CorrelatedOperationsShowActionNameAndCronRatherThanOpaqueAutomationGuid()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async s =>
        {
            var automation = s.GetRequiredService<AutomationService>(); var db = s.GetRequiredService<StanzaDbContext>();
            var action = await automation.SaveActionAsync(h.Root, null, 0, "Nightly reset", true, "queue", new([new("restart", h.Deployment)], DeploymentId: h.Deployment));
            var run = await automation.QueueActionAsync(h.Root, action.Id);
            await db.Set<AutomationRun>().Where(x => x.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Source, "schedule"));
            db.Add(new OperationEntity { Id = Guid.NewGuid(), ActorId = h.Root, DeploymentId = h.Deployment, AgentId = h.Agent, Kind = "restart", Trigger = "automation:" + run.Id.ToString("N") });
            await db.SaveChangesAsync();
            var rows = await s.GetRequiredService<OperationsOverviewService>().ListAsync(h.Root, null);
            Assert.Equal(2, rows.Count); Assert.All(rows, x => { Assert.Equal("Nightly reset", x.ActionName); Assert.Equal("Cron", x.Trigger); Assert.Equal(run.Id, x.ActionRunId); });
            return true;
        });
    }
}
