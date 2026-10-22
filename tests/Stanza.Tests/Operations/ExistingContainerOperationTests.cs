using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Deployments;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class ExistingContainerOperationTests
{
    [Theory]
    [InlineData("start", "deployment.start")]
    [InlineData("stop", "deployment.stop")]
    [InlineData("restart", "deployment.restart")]
    [InlineData("remove", "deployment.remove")]
    [InlineData("status", "deployment.read")]
    [InlineData("logs", "logs.read")]
    [InlineData("container-exec", "container.execute")]
    public async Task Existing_container_operations_use_actual_placement_and_applied_revision_without_fetching_sources_or_borrowing_credentials(string kind, string action)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var credential = await h.GitCredentialAsync(); await h.AddSourceAsync(credential.Id);
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", "[]",
            request.Sources?.ToDictionary(s => s.Destination, _ => new string('a', 40))));
        var applied = await h.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(applied.Id, "Succeeded"); await h.StopWorkerAsync();
        var desiredAgent = await h.AddAgentAsync();
        await h.InScope(async services =>
        {
            var method = typeof(DeploymentDraftService).GetMethod("ReassignPortablePlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task<DeploymentDraftSummary>)method.Invoke(services.GetRequiredService<DeploymentDraftService>(), [h.Root, h.Deployment, 1L, desiredAgent, CancellationToken.None])!;
            return true;
        });
        await h.AuthorizeUserAsync(action: action); // Deliberately no credential.use or desired-Agent access.
        var execution = kind == "container-exec" ? new AgentExecution("/usr/bin/true", [], Service: "app") : null;
        var queued = await h.EnqueueAsync(kind, h.User, execution: execution); var row = await h.RowAsync(queued.Id); var captured = h.Decode(row);
        Assert.Equal(h.Agent, queued.AgentId); Assert.Equal(1, row.Revision);
        Assert.Empty(captured.Files); Assert.Empty(captured.EnvironmentFiles); Assert.Empty(captured.Sources!); Assert.Empty(captured.Registries!);
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        Assert.Contains(h.Dispatcher.Requests, x => x.Request.Id == queued.Id && x.Agent == h.Agent);
    }
}
