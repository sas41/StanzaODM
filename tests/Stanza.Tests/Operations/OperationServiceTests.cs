using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class OperationServiceTests
{
    [Fact]
    public async Task Durable_encrypted_snapshot_captures_raw_global_agent_deployment_environment_in_order_without_reveal_grants()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.AuthorizeUserAsync();
        var credential = await h.GitCredentialAsync();
        await h.AddSourceAsync(credential.Id);
        await h.GrantAsync("credential.use", "credential", credential.Id);
        var queued = await h.EnqueueAsync(actor: h.User, trigger: "schedule:nightly");
        Assert.Equal("Queued", queued.State); Assert.Equal(h.User, queued.ActorId); Assert.Equal("schedule:nightly", queued.Trigger);
        var row = await h.RowAsync(queued.Id);
        var request = h.Decode(row);
        Assert.Equal(new[] { "global.env", "agent.env", "deployment.env" }, request.EnvironmentFiles.Select(x => x.Name));
        Assert.Equal("SHARED=global\nGLOBAL_SECRET=global-secret\n", request.EnvironmentFiles[0].Text);
        Assert.Equal("SHARED=agent\nAGENT_SECRET=agent-secret\n", request.EnvironmentFiles[1].Text);
        Assert.Equal("SHARED=deployment\nDEPLOYMENT_SECRET=deployment-secret\n", request.EnvironmentFiles[2].Text);
        Assert.Equal("git-password-secret", Assert.Single(request.Sources!).Password);
        Assert.Equal(1, row.Revision); Assert.Equal(h.Agent, row.AgentId);
        Assert.Equal("# compose-secret\nservices: {app: {image: example/app:latest}}\n", Encoding.UTF8.GetString(Assert.Single(request.Files).Content));
        Assert.All(new[] { "compose-secret", "global-secret", "agent-secret", "deployment-secret", "git-password-secret" },
            secret => Assert.DoesNotContain(secret, row.ProtectedRequest));
        Assert.DoesNotContain("git-password-secret", request.ToString());
        await h.InScope(async services =>
        {
            var access = services.GetRequiredService<AccessService>();
            Assert.False(await access.IsAllowedAsync(h.User, "environment.reveal", "instance", null));
            Assert.False(await access.IsAllowedAsync(h.User, "environment.reveal", "agent", h.Agent));
            Assert.False(await access.IsAllowedAsync(h.User, "environment.reveal", "deployment", h.Deployment));
            Assert.False(await access.IsAllowedAsync(h.User, "files.read", "deployment", h.Deployment));
            Assert.False(await access.IsAllowedAsync(h.User, "credential.reveal", "credential", credential.Id));
            return true;
        });
    }

    [Fact]
    public async Task Editing_all_environment_scopes_and_managed_files_does_not_mutate_an_existing_request()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var queued = await h.EnqueueAsync();
        var before = h.Decode(await h.RowAsync(queued.Id));
        await h.InScope(async services =>
        {
            var settings = services.GetRequiredService<InstanceSettingsService>();
            await settings.SaveEditorAsync(h.Root, null, "GLOBAL_SECRET=changed\n", 1);
            await services.GetRequiredService<AgentAdministrationService>().SaveEnvironmentAsync(h.Root, h.Agent, new("AGENT_SECRET=changed\n", 0));
            await services.GetRequiredService<DeploymentDraftService>().SaveAsync(h.Root, h.Deployment, new("App changed", h.Agent, 1,
                "services: {changed: {image: example/changed}}", "DEPLOYMENT_SECRET=changed\n"));
            return true;
        });
        var after = h.Decode(await h.RowAsync(queued.Id));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        var next = h.Decode(await h.RowAsync((await h.EnqueueAsync()).Id));
        Assert.Equal("GLOBAL_SECRET=changed\n", next.EnvironmentFiles[0].Text);
        Assert.Equal("AGENT_SECRET=changed\n", next.EnvironmentFiles[1].Text);
        Assert.Equal("DEPLOYMENT_SECRET=changed\n", next.EnvironmentFiles[2].Text);
        Assert.NotEqual(Encoding.UTF8.GetString(before.Files[0].Content), Encoding.UTF8.GetString(next.Files[0].Content));
    }

    [Fact]
    public async Task Request_ciphertext_is_bound_to_operation_identity_and_durable_after_context_disposal()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var first = await h.EnqueueAsync(); var second = await h.EnqueueAsync("validate");
        var row = await h.RowAsync(first.Id);
        Assert.Equal(first.Id, h.Decode(row).Id);
        Assert.Throws<CryptographicException>(() => h.Protection.CreateProtector("Stanza.Operation.v1", second.Id.ToString("N")).Unprotect(row.ProtectedRequest));
        Assert.Throws<CryptographicException>(() => new EphemeralDataProtectionProvider().CreateProtector("Stanza.Operation.v1", first.Id.ToString("N")).Unprotect(row.ProtectedRequest));
    }

    [Theory]
    [InlineData("validate", "deployment.validate")]
    [InlineData("build", "deployment.build")]
    [InlineData("pull", "deployment.deploy")]
    [InlineData("deploy", "deployment.deploy")]
    [InlineData("start", "deployment.start")]
    [InlineData("stop", "deployment.stop")]
    [InlineData("restart", "deployment.restart")]
    [InlineData("remove", "deployment.remove")]
    [InlineData("status", "deployment.read")]
    [InlineData("logs", "logs.read")]
    [InlineData("container-exec", "container.execute")]
    [InlineData("image-digests", "automation.read")]
    public async Task Every_operation_requires_its_exact_deployment_action_and_target_agent_grants(string kind, string action)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var other = await h.AddDeploymentAsync();
        await h.GrantAsync(action, "deployment", other);
        await h.GrantAsync("agent.read", "agent", h.Agent);
        var execution = kind == "container-exec" ? new AgentExecution("/usr/bin/true", [], Service: "app") : null;
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(kind, h.User, execution: execution));
        await h.GrantAsync(action, "deployment", h.Deployment);
        if (action != "deployment.read") await h.GrantAsync("deployment.read", "deployment", h.Deployment);
        var saved = await h.EnqueueAsync(kind, h.User, execution: execution);
        Assert.Equal(kind, saved.Kind); Assert.Equal(h.Agent, saved.AgentId);
        var anotherAgent = await h.AddAgentAsync();
        await h.GrantAsync("deployment.move", "deployment", h.Deployment);
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(kind, h.User, target: anotherAgent, execution: execution));
        await h.GrantAsync("agent.read", "agent", anotherAgent);
        var moved = await h.EnqueueAsync(kind, h.User, target: anotherAgent, execution: execution);
        Assert.Equal(anotherAgent, moved.AgentId);
    }

    [Fact]
    public async Task Alternate_agent_uses_destination_environment_and_requires_separate_move_permission()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var destination = await h.AddAgentAsync(environment: "DESTINATION_SECRET=destination-secret\n");
        await h.AuthorizeUserAsync();
        await h.GrantAsync("agent.read", "agent", destination);
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(actor: h.User, target: destination));
        await h.GrantAsync("deployment.move", "deployment", h.Deployment);
        var queued = await h.EnqueueAsync(actor: h.User, target: destination);
        Assert.Equal("DESTINATION_SECRET=destination-secret\n", h.Decode(await h.RowAsync(queued.Id)).EnvironmentFiles[1].Text);
    }

    [Fact]
    public async Task Credential_reveal_is_not_use_and_no_request_is_persisted_on_denial()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.AuthorizeUserAsync();
        var credential = await h.GitCredentialAsync(); await h.AddSourceAsync(credential.Id);
        await h.GrantAsync("credential.reveal", "credential", credential.Id);
        await Assert.ThrowsAsync<SourceAccessException>(() => h.EnqueueAsync(actor: h.User));
        await h.InScope(async services =>
        {
            Assert.Empty(await services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().ToListAsync());
            Assert.Empty(await services.GetRequiredService<StanzaDbContext>().Set<AuditEvent>().Where(x => x.Action == "operation.queued").ToListAsync());
            return true;
        });
        await h.GrantAsync("credential.use", "credential", credential.Id);
        Assert.Equal("Queued", (await h.EnqueueAsync(actor: h.User)).State);
    }

    [Fact]
    public async Task Registry_secrets_are_snapshotted_with_use_not_reveal_and_metadata_remains_redacted()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.AuthorizeUserAsync(); var credential = await h.RegistryCredentialAsync();
        await h.InScope(async services => { await services.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [credential.Id], []); return true; });
        await Assert.ThrowsAsync<SourceAccessException>(() => h.EnqueueAsync(actor: h.User));
        await h.GrantAsync("credential.use", "credential", credential.Id);
        var queued = await h.EnqueueAsync(actor: h.User);
        Assert.Equal("registry-password-secret", Assert.Single(h.Decode(await h.RowAsync(queued.Id)).Registries!).Password);
        Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output);
        Assert.DoesNotContain("registry-password-secret", (await h.DetailAsync(queued.Id, h.User)).ToString());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("revoked")]
    [InlineData("rejected")]
    public async Task Pending_or_revoked_agents_cannot_receive_a_new_operation(string status)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async services =>
        {
            await services.GetRequiredService<StanzaDbContext>().Set<AgentEntity>().Where(x => x.Id == h.Agent).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status)); return true;
        });
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync());
    }

    [Theory]
    [InlineData("arbitrary-shell")]
    [InlineData("host-exec")]
    public async Task Unsupported_or_host_operations_cannot_borrow_a_deployment_identity(string kind)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(kind, execution: new("/usr/bin/true", [])));
    }

    [Fact]
    public async Task Host_execution_is_opt_in_and_requires_its_exact_agent_capability()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var submission = new OperationSubmission(h.User, null, "host-exec", AgentId: h.Agent, Execution: new("/usr/bin/true", []));
        Task<OperationSummary> Enqueue() => h.InScope(services => services.GetRequiredService<OperationService>().EnqueueAsync(submission));
        await Assert.ThrowsAsync<OperationException>(Enqueue);
        await h.InScope(async services =>
        {
            var policy = services.GetRequiredService<OperationalPolicyService>(); var edit = await policy.GetAsync();
            await policy.SaveAsync(h.Root, edit with { Policy = edit.Policy with { AllowHostActions = true } }); return true;
        });
        await Assert.ThrowsAsync<OperationException>(Enqueue);
        await h.GrantAsync("host.execute", "agent", h.Agent);
        var saved = await Enqueue(); Assert.Null(saved.DeploymentId); Assert.Equal(h.Agent, saved.AgentId);
        Assert.Empty(h.Decode(await h.RowAsync(saved.Id)).Files);
    }

    [Fact]
    public async Task Scoped_agent_policy_is_captured_instead_of_global_defaults()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async services =>
        {
            await services.GetRequiredService<AgentPolicyService>().SaveAsync(h.Root, new(h.Agent,
                new(true, 37, "unix:///run/selected-docker.sock"), 0)); return true;
        });
        var policy = h.Decode(await h.RowAsync((await h.EnqueueAsync()).Id)).Policy;
        Assert.Equal(new AgentRuntimePolicy(true, 3600, "unix:///run/selected-docker.sock"), policy);
    }

    [Fact]
    public async Task Queued_cancellation_is_durable_audited_and_requires_cancel_not_deploy_permission()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.AuthorizeUserAsync(); var queued = await h.EnqueueAsync(actor: h.User);
        await Assert.ThrowsAsync<OperationException>(() => h.CancelAsync(queued.Id, h.User));
        await h.GrantAsync("operations.cancel", "deployment", h.Deployment);
        await h.CancelAsync(queued.Id, h.User);
        var row = await h.RowAsync(queued.Id);
        Assert.Equal("Cancelled", row.State); Assert.Equal(h.Clock.GetUtcNow().ToUnixTimeSeconds(), row.CompletedAt);
        await Assert.ThrowsAsync<OperationException>(() => h.CancelAsync(queued.Id, h.User));
        await h.InScope(async services =>
        {
            var audit = await services.GetRequiredService<StanzaDbContext>().Set<AuditEvent>().Where(x => x.Resource == queued.Id.ToString()).ToListAsync();
            Assert.Contains(audit, x => x.Action == "operation.queued" && x.ActorId == h.User);
            Assert.Contains(audit, x => x.Action == "operation.cancel" && x.Result == "cancelled_before_dispatch"); return true;
        });
    }

    [Fact]
    public async Task Running_cancellation_requests_do_not_claim_the_remote_work_was_rolled_back()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var queued = await h.EnqueueAsync();
        await h.SetStateAsync(queued.Id, "Running", leaseUntil: h.Clock.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds());
        await h.CancelAsync(queued.Id);
        var row = await h.RowAsync(queued.Id);
        Assert.Equal("CancellationRequested", row.State); Assert.Null(row.CompletedAt); Assert.False(OperationService.Terminal(row.State));
    }

    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("Failed", true)]
    [InlineData("Unknown", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Queued", false)]
    [InlineData("Running", false)]
    [InlineData("CancellationRequested", false)]
    public void Terminal_classification_never_treats_a_cancellation_request_as_finished(string state, bool terminal) =>
        Assert.Equal(terminal, OperationService.Terminal(state));
}
