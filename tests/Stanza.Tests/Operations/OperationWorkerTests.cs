using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class OperationWorkerTests
{
    [Fact]
    public async Task Completion_encrypts_response_preserves_resolved_build_identity_and_audits_only_fixed_metadata()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var git = await h.GitCredentialAsync(); await h.AddSourceAsync(git.Id);
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", "output-secret",
            new Dictionary<string, string> { ["sources/app"] = new('a', 40) }, new Dictionary<string, string> { ["app"] = "sha256:image-identity" }));
        var queued = await h.EnqueueAsync(); await h.StartWorkerAsync();
        var completed = await h.WaitStateAsync(queued.Id, "Succeeded");
        Assert.NotNull(completed.CompletedAt); Assert.NotNull(completed.ProtectedResponse);
        Assert.DoesNotContain("output-secret", completed.ProtectedResponse);
        var response = JsonSerializer.Deserialize<AgentWorkResponse>(h.Protection.CreateProtector("Stanza.Operation.v1", queued.Id.ToString("N")).Unprotect(completed.ProtectedResponse!))!;
        Assert.Equal(new string('a', 40), response.ResolvedSources!["sources/app"]);
        Assert.Equal("sha256:image-identity", response.ResolvedImages!["app"]);
        Assert.Equal("output-secret", (await h.DetailAsync(queued.Id)).Output);
        await h.WaitForAuditAsync(queued.Id, "operation.finished");
        await h.InScope(async services =>
        {
            var audit = await services.GetRequiredService<StanzaDbContext>().Set<AuditEvent>().Where(x => x.Resource == queued.Id.ToString()).ToListAsync();
            Assert.Contains(audit, x => x.Action == "operation.queued");
            Assert.Contains(audit, x => x.Action == "operation.finished" && x.Result == "Succeeded:ok");
            var serialized = JsonSerializer.Serialize(audit);
            foreach (var secret in new[] { "output-secret", "git-password-secret", "global-secret", "agent-secret", "deployment-secret", "compose-secret" })
                Assert.DoesNotContain(secret, serialized);
            return true;
        });
    }

    [Fact]
    public async Task Operation_metadata_does_not_grant_output_or_cross_deployment_access()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var queued = await h.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        var other = await h.AddDeploymentAsync();
        await h.GrantAsync("deployment.read", "deployment", other);
        await Assert.ThrowsAsync<OperationException>(() => h.DetailAsync(queued.Id, h.User));
        await h.GrantAsync("deployment.read", "deployment", h.Deployment);
        Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output);
        await h.GrantAsync("logs.read", "deployment", other);
        Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output);
        await h.GrantAsync("logs.read", "deployment", h.Deployment);
        Assert.Equal("output-secret", (await h.DetailAsync(queued.Id, h.User)).Output);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("agent-read")]
    [InlineData("disabled-account")]
    [InlineData("agent-approval")]
    [InlineData("agent-execution")]
    public async Task Current_access_and_agent_trust_are_rechecked_before_queued_dispatch(string revoke)
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AuthorizeUserAsync();
        var queued = await h.EnqueueAsync(actor: h.User);
        await h.InScope(async services =>
        {
            if (revoke is "action" or "agent-read")
            {
                var action = revoke == "action" ? "deployment.deploy" : "agent.read";
                var grant = await services.GetRequiredService<StanzaDbContext>().Grants.AsNoTracking().SingleAsync(x => x.AccountId == h.User && x.Action == action);
                await services.GetRequiredService<AccessService>().RemoveGrantAsync(h.Root, grant.Id);
            }
            else if (revoke == "disabled-account") await services.GetRequiredService<AccountService>().SetEnabledAsync(h.Root, h.User, false);
            else if (revoke == "agent-approval") await services.GetRequiredService<StanzaDbContext>().Set<AgentEntity>().Where(x => x.Id == h.Agent)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "revoked"));
            else await services.GetRequiredService<AgentPolicyService>().SaveAsync(h.Root, new(h.Agent, new(false), 0));
            return true;
        });
        await h.StartWorkerAsync(); var failed = await h.WaitStateAsync(queued.Id, "Failed");
        Assert.Empty(h.Dispatcher.Requests); Assert.Null(failed.ProtectedResponse);
    }

    [Theory]
    [InlineData("git-credential")]
    [InlineData("git-reference")]
    [InlineData("git-removed")]
    [InlineData("registry-credential")]
    [InlineData("registry-binding")]
    [InlineData("credential-use")]
    public async Task Source_or_secret_changes_and_use_revocation_invalidate_existing_queued_snapshots(string change)
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AuthorizeUserAsync();
        var git = await h.GitCredentialAsync(); var source = await h.AddSourceAsync(git.Id);
        var registry = await h.RegistryCredentialAsync();
        await h.InScope(async services => { await services.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [registry.Id], []); return true; });
        var use = await h.GrantAsync("credential.use", "credential", git.Id);
        await h.GrantAsync("credential.use", "credential", registry.Id);
        var queued = await h.EnqueueAsync(actor: h.User);
        await h.InScope(async services =>
        {
            var sources = services.GetRequiredService<SourceService>();
            if (change == "git-credential") await sources.SaveCredentialAsync(h.Root, git.Id, git.Version, new("Git", "git", OperationTestHarness.GitUrl, "new-user", "new-git-secret"));
            if (change == "git-reference") await sources.SaveGitAsync(h.Root, h.Deployment, source.Id, source.Version, new(source.Url, "release", source.Destination, source.CredentialId));
            if (change == "git-removed") await sources.DeleteGitAsync(h.Root, h.Deployment, source.Id, source.Version);
            if (change == "registry-credential") await sources.SaveCredentialAsync(h.Root, registry.Id, registry.Version, new("Registry", "registry", registry.Endpoint, "new-user", "new-registry-secret"));
            if (change == "registry-binding") await sources.SetRegistriesAsync(h.Root, h.Deployment, [], [registry.Id]);
            if (change == "credential-use") await services.GetRequiredService<AccessService>().RemoveGrantAsync(h.Root, use.Id);
            return true;
        });
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Failed");
        Assert.Empty(h.Dispatcher.Requests);
        Assert.Equal("git-password-secret", Assert.Single(h.Decode(await h.RowAsync(queued.Id)).Sources!).Password);
    }

    [Fact]
    public async Task Revoking_alternate_target_move_permission_prevents_dispatch_of_the_queued_cross_agent_operation()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AuthorizeUserAsync();
        var other = await h.AddAgentAsync(); await h.GrantAsync("agent.read", "agent", other);
        var move = await h.GrantAsync("deployment.move", "deployment", h.Deployment);
        var queued = await h.EnqueueAsync(actor: h.User, target: other);
        await h.RevokeAsync(move.Id);
        await h.StartWorkerAsync(); var row = await h.WaitStateAsync(queued.Id, "Succeeded", "Failed");
        Assert.Equal("Failed", row.State);
        Assert.Empty(h.Dispatcher.Requests);
    }

    [Fact]
    public async Task Same_deployment_is_serialized_while_different_deployments_can_run_in_parallel()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var other = await h.AddDeploymentAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, token) => { await release.Task.WaitAsync(token); return new(request.Id, true, "ok"); };
        var first = await h.EnqueueAsync(); h.Clock.Advance(TimeSpan.FromSeconds(1));
        var conflicting = await h.EnqueueAsync("restart"); h.Clock.Advance(TimeSpan.FromSeconds(1));
        var independent = await h.EnqueueAsync(deployment: other);
        await h.StartWorkerAsync();
        await h.Dispatcher.WaitForCountAsync(2);
        Assert.Equal("Running", (await h.RowAsync(first.Id)).State);
        Assert.Equal("Queued", (await h.RowAsync(conflicting.Id)).State);
        Assert.Equal("Running", (await h.RowAsync(independent.Id)).State);
        Assert.Equal(2, h.Dispatcher.Requests.Select(x => x.Request.DeploymentId).Distinct().Count());
        release.SetResult();
        await h.WaitStateAsync(first.Id, "Succeeded"); await h.WaitStateAsync(independent.Id, "Succeeded");
        await h.NextTickAsync(); await h.WaitStateAsync(conflicting.Id, "Succeeded");
        Assert.Equal(3, h.Dispatcher.Requests.Count);
    }

    [Fact]
    public async Task Offline_agent_leaves_operations_queued_and_does_not_disclose_the_snapshot()
    {
        await using var h = await OperationTestHarness.CreateAsync(); h.Dispatcher.Connected = false;
        var queued = await h.EnqueueAsync(); await h.StartWorkerAsync();
        await OperationTestHarness.UntilAsync(() => Task.FromResult(h.Clock.PendingTimerCount > 0));
        await h.NextTickAsync();
        Assert.Equal("Queued", (await h.RowAsync(queued.Id)).State); Assert.Empty(h.Dispatcher.Requests);
    }

    [Fact]
    public async Task Queued_cancellation_never_dispatches_and_running_cancellation_becomes_uncertain_not_false_rollback()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var cancelled = await h.EnqueueAsync(); await h.CancelAsync(cancelled.Id);
        h.Dispatcher.Handler = async (_, _, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("unreachable"); };
        var running = await h.EnqueueAsync();
        await h.StartWorkerAsync(); await h.Dispatcher.WaitForCountAsync(1);
        Assert.Equal(running.Id, Assert.Single(h.Dispatcher.Requests).Request.Id);
        await h.CancelAsync(running.Id);
        Assert.Equal("CancellationRequested", (await h.RowAsync(running.Id)).State);
        await h.NextTickAsync();
        var unknown = await h.WaitStateAsync(running.Id, "Unknown");
        Assert.Equal("execution_outcome_uncertain", unknown.Code);
        Assert.Equal("Cancelled", (await h.RowAsync(cancelled.Id)).State);
        Assert.Single(h.Dispatcher.Requests);
    }

    [Theory]
    [InlineData("transport-exception")]
    [InlineData("agent-uncertain")]
    public async Task Uncertain_dispatch_outcomes_are_never_replayed_and_block_new_mutations_until_reconciliation(string failure)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        h.Dispatcher.Handler = (_, request, _) => failure == "transport-exception"
            ? Task.FromException<AgentWorkResponse>(new AgentUnavailableException("secret transport detail"))
            : Task.FromResult(new AgentWorkResponse(request.Id, false, "uncertain", "secret-output"));
        var original = await h.EnqueueAsync(); await h.StartWorkerAsync();
        await h.WaitStateAsync(original.Id, "Unknown");
        var blocked = await h.EnqueueAsync("restart");
        await h.NextTickAsync();
        Assert.Equal("Queued", (await h.RowAsync(blocked.Id)).State); Assert.Single(h.Dispatcher.Requests);
        await h.StopWorkerAsync(); await h.StartWorkerAsync();
        await OperationTestHarness.UntilAsync(() => Task.FromResult(h.Clock.PendingTimerCount > 0));
        Assert.Equal("Unknown", (await h.RowAsync(original.Id)).State); Assert.Single(h.Dispatcher.Requests);
        await h.CancelAsync(blocked.Id);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("CancellationRequested")]
    public async Task Expired_worker_leases_become_unknown_after_restart_and_are_not_retried(string interruptedState)
    {
        await using var h = await OperationTestHarness.CreateAsync(); var original = await h.EnqueueAsync();
        await h.SetStateAsync(original.Id, interruptedState, leaseUntil: h.Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds());
        await h.StartWorkerAsync(); var unknown = await h.WaitStateAsync(original.Id, "Unknown");
        Assert.Equal("worker_interrupted", unknown.Code); Assert.NotNull(unknown.CompletedAt); Assert.Empty(h.Dispatcher.Requests);
    }

    [Fact]
    public async Task Fresh_status_on_same_agent_allows_explicit_reconciliation_without_replaying_uncertain_work()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var unknown = await h.EnqueueAsync();
        await h.SetStateAsync(unknown.Id, "Unknown", h.Clock.GetUtcNow().ToUnixTimeSeconds());
        await Assert.ThrowsAsync<OperationException>(() => h.ReconcileAsync(unknown.Id));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var status = await h.EnqueueAsync("status"); await h.StartWorkerAsync(); await h.WaitStateAsync(status.Id, "Succeeded");
        await h.ReconcileAsync(unknown.Id);
        var row = await h.RowAsync(unknown.Id);
        Assert.Equal("Failed", row.State); Assert.Equal("reconciled_not_retried", row.Code);
        Assert.Equal(status.Id, Assert.Single(h.Dispatcher.Requests).Request.Id);
    }

    [Fact]
    public async Task Successful_status_from_another_agent_cannot_reconcile_an_uncertain_outcome()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var other = await h.AddAgentAsync();
        var unknown = await h.EnqueueAsync(); await h.SetStateAsync(unknown.Id, "Unknown", h.Clock.GetUtcNow().ToUnixTimeSeconds());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var status = await h.EnqueueAsync("status", target: other); await h.StartWorkerAsync(); await h.WaitStateAsync(status.Id, "Succeeded");
        await Assert.ThrowsAsync<OperationException>(() => h.ReconcileAsync(unknown.Id));
        Assert.Equal("Unknown", (await h.RowAsync(unknown.Id)).State);
    }

    [Fact]
    public async Task Status_observed_before_an_operation_became_uncertain_is_not_fresh_reconciliation_evidence()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var unknown = await h.EnqueueAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var staleStatus = await h.EnqueueAsync("status"); await h.SetStateAsync(staleStatus.Id, "Succeeded", h.Clock.GetUtcNow().ToUnixTimeSeconds());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.SetStateAsync(unknown.Id, "Unknown", h.Clock.GetUtcNow().ToUnixTimeSeconds());
        await Assert.ThrowsAsync<OperationException>(() => h.ReconcileAsync(unknown.Id));
        Assert.Equal("Unknown", (await h.RowAsync(unknown.Id)).State);
    }

    [Fact]
    public async Task Unexpected_agent_codes_and_exception_messages_cannot_leak_into_operation_audit()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, false, "password=secret-token\n", "sensitive-output"));
        var queued = await h.EnqueueAsync(); await h.StartWorkerAsync();
        var failed = await h.WaitStateAsync(queued.Id, "Failed"); Assert.Equal("agent_result", failed.Code);
        await h.WaitForAuditAsync(queued.Id, "operation.finished");
        await h.InScope(async services =>
        {
            var events = await services.GetRequiredService<StanzaDbContext>().Set<AuditEvent>().ToListAsync();
            var serialized = JsonSerializer.Serialize(events);
            Assert.DoesNotContain("secret-token", serialized); Assert.DoesNotContain("sensitive-output", serialized); return true;
        });
    }

    [Fact]
    public async Task Successful_lifecycle_records_saved_revision_desired_state_and_observation_timestamp()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var queued = await h.EnqueueAsync("stop"); await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        await h.InScope(async services =>
        {
            var runtime = await services.GetRequiredService<StanzaDbContext>().Set<DeploymentRuntimeEntity>().AsNoTracking().SingleAsync();
            Assert.Equal(h.Deployment, runtime.DeploymentId); Assert.Equal(h.Agent, runtime.AgentId); Assert.Equal(1, runtime.Revision);
            Assert.Equal("stopped", runtime.DesiredState); Assert.Equal(h.Clock.GetUtcNow().ToUnixTimeSeconds(), runtime.ObservedAt); return true;
        });
    }

    [Fact]
    public async Task Fresh_status_updates_observed_runtime_instead_of_retaining_an_obsolete_success_assumption()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async services =>
        {
            services.GetRequiredService<StanzaDbContext>().Add(new DeploymentRuntimeEntity { DeploymentId = h.Deployment, AgentId = h.Agent, Revision = 1,
                DesiredState = "running", ObservedState = "running", ObservedAt = h.Clock.GetUtcNow().AddMinutes(-5).ToUnixTimeSeconds() });
            await services.GetRequiredService<StanzaDbContext>().SaveChangesAsync(); return true;
        });
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", "[]"));
        var status = await h.EnqueueAsync("status"); await h.StartWorkerAsync(); await h.WaitStateAsync(status.Id, "Succeeded");
        await h.InScope(async services =>
        {
            var runtime = await services.GetRequiredService<StanzaDbContext>().Set<DeploymentRuntimeEntity>().AsNoTracking().SingleAsync();
            Assert.Equal("running", runtime.DesiredState);
            Assert.NotEqual("running", runtime.ObservedState);
            Assert.Equal(h.Clock.GetUtcNow().ToUnixTimeSeconds(), runtime.ObservedAt); return true;
        });
    }
}
