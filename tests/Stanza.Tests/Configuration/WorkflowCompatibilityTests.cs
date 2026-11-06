using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Configuration;

public sealed class WorkflowCompatibilityTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FreshAndExistingInstallationsPreserveBothActionFormatsAndRememberedValues(bool upgrade)
    {
        await using var store = await ConfigurationTestStore.CreateAsync(); await using var db = store.Context();
        var automation = new AutomationService(db, new(store.Access(db)), store.Protection, store.Clock);
        var deployment = Guid.NewGuid();
        var plans = new[] {
            new ActionPlan([new("deploy", deployment)], [new("BRANCH", Default: "main", Input: "branch", Source: "sources/app")], deployment, "Customized", 37, "Confirm?", false),
            new ActionPlan([new("container-script", deployment, Execution: new AgentExecution("/bin/bash", [], Service: "app", Mode: "script", Script: "echo original\r\n"))], DeploymentId: deployment)
        };
        var actions = plans.Select((plan, i) => new ActionDefinition { Id = Guid.NewGuid(), Name = "Action " + i, NormalizedName = "ACTION " + i,
            OwnerId = store.Owner, ScopeDeploymentId = deployment, ProtectedSteps = automation.Protect(plan), Concurrency = "reject", Revision = 9, Enabled = true }).ToArray();
        db.AddRange(actions);
        db.Add(new ActionInputMemory { ActionId = actions[0].Id, DeploymentId = deployment, ActionRevision = 9,
            ProtectedValues = automation.Protect(new Dictionary<string, string> { ["BRANCH"] = "feature/testing" }) });
        db.Settings.Add(new() { Key = "schema-version", Value = upgrade ? DatabaseUpgrader.CurrentVersion.ToString() : "0" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await DatabaseUpgrader.UpgradeAsync(db, protection: store.Protection);
        await DatabaseUpgrader.UpgradeAsync(db, protection: store.Protection);
        for (var i = 0; i < actions.Length; i++)
        {
            var action = await db.Set<ActionDefinition>().AsNoTracking().SingleAsync(a => a.Id == actions[i].Id);
            Assert.Equal(actions[i].ProtectedSteps, action.ProtectedSteps); Assert.Equal("reject", action.Concurrency); Assert.Equal(9, action.Revision);
            var plan = automation.Unprotect<ActionPlan>(action.ProtectedSteps); AutomationRules.Validate(plan);
            var model = ActionEditorModel.From(action, plan);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plan, AutomationRules.Json), System.Text.Json.JsonSerializer.Serialize(model.ToPlan(plan), AutomationRules.Json));
        }
        var remembered = await db.Set<ActionInputMemory>().AsNoTracking().SingleAsync();
        Assert.Equal("feature/testing", automation.Unprotect<Dictionary<string, string>>(remembered.ProtectedValues)["BRANCH"]);
    }
}
