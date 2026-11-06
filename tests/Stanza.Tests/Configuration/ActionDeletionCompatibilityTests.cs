using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Configuration;

public sealed class ActionDeletionCompatibilityTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FreshAndExistingInstallationsCanDeleteEitherActionFormatWithoutRewritingHistoryOrOtherSettings(bool existing)
    {
        await using var store = await ConfigurationTestStore.CreateAsync(); await using var db = store.Context();
        var authorization = new AutomationAuthorization(store.Access(db)); var automation = new AutomationService(db, authorization, store.Protection, store.Clock);
        var deployment = Guid.NewGuid();
        var legacy = await automation.SaveActionAsync(store.Owner, null, 0, "Legacy", false, "reject", new([new("deploy", deployment)], DeploymentId: deployment));
        var script = await automation.SaveActionAsync(store.Owner, null, 0, "Script", true, "queue", new([new("container-script", deployment,
            Execution: new AgentExecution("/bin/bash", [], Mode: "script", Script: "echo saved\r\n", Service: "app"))], DeploymentId: deployment));
        var run = new AutomationRun { Id = Guid.NewGuid(), OwnerId = store.Owner, RequestedBy = store.Owner, ActionId = legacy.Id,
            State = "succeeded", OccurrenceKey = "existing-run", ProtectedPlan = legacy.ProtectedSteps, ProtectedParameters = automation.Protect(new Dictionary<string, string>()) };
        db.Add(run); db.Settings.Add(new() { Key = "custom-setting", Value = "preserved" });
        db.Settings.Add(new() { Key = "schema-version", Value = existing ? DatabaseUpgrader.CurrentVersion.ToString() : "0" }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await DatabaseUpgrader.UpgradeAsync(db, protection: store.Protection); await DatabaseUpgrader.UpgradeAsync(db, protection: store.Protection);
        Assert.Equal(legacy.ProtectedSteps, (await db.Set<ActionDefinition>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProtectedSteps);
        Assert.Equal(script.ProtectedSteps, (await db.Set<ActionDefinition>().AsNoTracking().SingleAsync(x => x.Id == script.Id)).ProtectedSteps);
        var deletion = new ActionDeletionService(db, automation, authorization);
        foreach (var action in new[] { legacy, script })
        {
            var preview = await deletion.PreviewAsync(store.Owner, action.Id);
            await deletion.DeleteAsync(store.Owner, action.Id, preview.Revision, preview.Fingerprint, true, false);
        }
        Assert.False(await db.Set<ActionDefinition>().AnyAsync());
        var retained = await automation.ReadRunAsync(store.Owner, run.Id);
        Assert.False(retained.ActionAvailable); Assert.Equal(legacy.ProtectedSteps, retained.Run.ProtectedPlan);
        Assert.Equal("preserved", (await db.Settings.SingleAsync(x => x.Key == "custom-setting")).Value);
        Assert.Equal(DatabaseUpgrader.CurrentVersion.ToString(), (await db.Settings.SingleAsync(x => x.Key == "schema-version")).Value);
    }
}
