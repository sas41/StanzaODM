using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class EnvironmentPreviewOperationTests
{
    [Fact]
    public async Task Preview_captures_ordered_raw_inputs_without_borrowing_credential_use_or_log_access()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var credential = await h.GitCredentialAsync(); await h.AddSourceAsync(credential.Id);
        await GrantPreview(h);
        var queued = await h.EnqueueAsync("environment-preview", h.User);
        var request = h.Decode(await h.RowAsync(queued.Id));
        Assert.Equal(new[] { "global.env", "agent.env", "deployment.env" }, request.EnvironmentFiles.Select(x => x.Name));
        Assert.Contains("global-secret", request.EnvironmentFiles[0].Text);
        Assert.Contains("agent-secret", request.EnvironmentFiles[1].Text);
        Assert.Contains("deployment-secret", request.EnvironmentFiles[2].Text);
        Assert.Single(request.Files); Assert.Empty(request.Sources!); Assert.Empty(request.Registries!);
        h.Dispatcher.Handler = (_, work, _) => Task.FromResult(new AgentWorkResponse(work.Id, true, "ok", "effective-environment-secret"));
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        Assert.Equal("effective-environment-secret", (await h.DetailAsync(queued.Id, h.User)).Output);
        await h.InScope(async services =>
        {
            var access = services.GetRequiredService<AccessService>();
            Assert.False(await access.IsAllowedAsync(h.User, "credential.use", "credential", credential.Id));
            Assert.False(await access.IsAllowedAsync(h.User, "logs.read", "deployment", h.Deployment)); return true;
        });
    }

    [Theory]
    [InlineData("deployment.read", "deployment")]
    [InlineData("agent.read", "agent")]
    [InlineData("environment.reveal", "instance")]
    [InlineData("environment.reveal", "agent")]
    [InlineData("environment.reveal", "deployment")]
    public async Task Every_scope_is_required_before_enqueue_and_dispatch_and_again_before_output_reveal(string action, string type)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var grants = await GrantPreview(h);
        var revoke = grants.Single(x => x.Action == action && x.ResourceType == type);
        var queued = await h.EnqueueAsync("environment-preview", h.User);
        h.Dispatcher.Handler = (_, work, _) => Task.FromResult(new AgentWorkResponse(work.Id, true, "ok", "effective-environment-secret"));
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded"); await h.StopWorkerAsync();
        Assert.Equal("effective-environment-secret", (await h.DetailAsync(queued.Id, h.User)).Output);
        var waiting = await h.EnqueueAsync("environment-preview", h.User);
        await h.RevokeAsync(revoke.Id);
        if (action == "deployment.read") await Assert.ThrowsAsync<OperationException>(() => h.DetailAsync(queued.Id, h.User));
        else Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output);
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync("environment-preview", h.User));
        await h.StartWorkerAsync(); await h.WaitStateAsync(waiting.Id, "Failed");
        Assert.DoesNotContain(h.Dispatcher.Requests, x => x.Request.Id == waiting.Id);
    }

    [Fact]
    public async Task Reveal_permissions_do_not_substitute_for_logs_read_on_other_operation_kinds()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await GrantPreview(h);
        await h.GrantAsync("deployment.deploy", "deployment", h.Deployment);
        var queued = await h.EnqueueAsync(actor: h.User); await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output);
        await h.GrantAsync("logs.read", "deployment", h.Deployment);
        Assert.Equal("output-secret", (await h.DetailAsync(queued.Id, h.User)).Output);
    }

    [Fact]
    public async Task Logs_read_and_deployment_reveal_cannot_reveal_inherited_global_or_agent_values()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.AuthorizeUserAsync(action: "deployment.read");
        await h.GrantAsync("logs.read", "deployment", h.Deployment);
        await h.GrantAsync("environment.reveal", "deployment", h.Deployment);
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync("environment-preview", h.User));
    }

    private static async Task<IReadOnlyList<GrantSummary>> GrantPreview(OperationTestHarness h) =>
    [
        await h.GrantAsync("deployment.read", "deployment", h.Deployment),
        await h.GrantAsync("agent.read", "agent", h.Agent),
        await h.GrantAsync("environment.reveal", "instance", null),
        await h.GrantAsync("environment.reveal", "agent", h.Agent),
        await h.GrantAsync("environment.reveal", "deployment", h.Deployment)
    ];
}
