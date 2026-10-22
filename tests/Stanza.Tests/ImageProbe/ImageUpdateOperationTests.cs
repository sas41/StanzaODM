using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Operations;

namespace Stanza.Tests.ImageProbe;

public sealed class ImageUpdateOperationTests
{
    [Theory]
    [InlineData("image-policy")]
    [InlineData("manual-image-policy")]
    public async Task Auto_redeploy_is_bound_to_applied_operation_and_reuses_exact_git_commit_with_fresh_authorized_credentials(string source)
    {
        await using var f = await Fixture.CreateAsync(source);
        var h = f.Harness;
        await h.InScope(async services =>
        {
            var sources = services.GetRequiredService<SourceService>();
            await sources.SaveCredentialAsync(h.Root, f.Git.Id, f.Git.Version, new("Git", "git", f.Git.Endpoint, "rotated-user", "rotated-git-secret"));
            await sources.SaveCredentialAsync(h.Root, f.Registry.Id, f.Registry.Version, new("Registry", "registry", f.Registry.Endpoint, "rotated-user", "rotated-registry-secret"));
            return true;
        });
        var queued = await f.EnqueueAsync(); var row = await h.RowAsync(queued.Id); var request = h.Decode(row);
        Assert.Equal(f.Applied.Id, row.ParentAppliedOperationId); Assert.Equal(1, row.Revision);
        Assert.Equal(new string('a', 40), Assert.Single(request.Sources!).Reference);
        Assert.Equal("rotated-git-secret", request.Sources![0].Password);
        Assert.Equal("rotated-registry-secret", Assert.Single(request.Registries!).Password);
        var applied = h.Decode(await h.RowAsync(f.Applied.Id));
        Assert.Equal(applied.Files[0].Content, request.Files[0].Content);
        Assert.Equal(applied.EnvironmentFiles, request.EnvironmentFiles);
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
        Assert.Contains(h.Dispatcher.Requests, x => x.Request.Id == queued.Id && x.Request.Sources![0].Reference == new string('a', 40));
        Assert.Null((await h.DetailAsync(queued.Id, h.User)).Output); // Use was authorized; reveal/log access was not borrowed.
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("global-environment")]
    [InlineData("agent-environment")]
    [InlineData("deployment-environment")]
    [InlineData("placement")]
    [InlineData("source-reference")]
    [InlineData("source-added")]
    [InlineData("credential-binding")]
    [InlineData("registry-credential-binding")]
    [InlineData("registry-binding")]
    [InlineData("pending-operation")]
    [InlineData("unknown-operation")]
    [InlineData("runtime-agent")]
    [InlineData("missing-metadata")]
    [InlineData("invalid-commit")]
    public async Task Pending_manual_changes_between_probe_and_enqueue_cannot_be_applied_by_an_image_policy(string change)
    {
        await using var f = await Fixture.CreateAsync();
        await f.MutateAsync(change);
        await Assert.ThrowsAsync<OperationException>(f.EnqueueAsync);
        await f.Harness.InScope(async services =>
        {
            Assert.False(await services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().AnyAsync(x => x.Trigger == "automation:" + f.Run.ToString("N")));
            return true;
        });
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("global-environment")]
    [InlineData("agent-environment")]
    [InlineData("deployment-environment")]
    [InlineData("placement")]
    [InlineData("source-reference")]
    [InlineData("source-added")]
    [InlineData("credential-binding")]
    [InlineData("registry-credential-binding")]
    [InlineData("registry-binding")]
    [InlineData("runtime-agent")]
    [InlineData("missing-metadata")]
    [InlineData("invalid-commit")]
    public async Task The_same_baseline_checks_run_again_immediately_before_dispatch(string change)
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var queued = await f.EnqueueAsync();
        await f.MutateAsync(change);
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Failed");
        Assert.DoesNotContain(h.Dispatcher.Requests, x => x.Request.Id == queued.Id);
    }

    [Theory]
    [InlineData("git-credential")]
    [InlineData("registry-credential")]
    [InlineData("credential-use")]
    [InlineData("policy-mode")]
    [InlineData("policy-disabled")]
    [InlineData("run-cancelled")]
    public async Task Queued_image_jobs_recheck_credentials_and_automation_policy_not_just_the_recorded_commit(string change)
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var queued = await f.EnqueueAsync();
        await h.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>(); var sources = services.GetRequiredService<SourceService>();
            if (change == "git-credential") await sources.SaveCredentialAsync(h.Root, f.Git.Id, 1, new("Git", "git", f.Git.Endpoint, "u", "new-secret"));
            if (change == "registry-credential") await sources.SaveCredentialAsync(h.Root, f.Registry.Id, 1, new("Registry", "registry", f.Registry.Endpoint, "u", "new-secret"));
            if (change == "credential-use")
            {
                var grant = await db.Grants.AsNoTracking().SingleAsync(x => x.AccountId == h.User && x.Action == "credential.use" && x.ResourceId == f.Git.Id);
                await services.GetRequiredService<Stanza.ControlPlane.Security.AccessService>().RemoveGrantAsync(h.Root, grant.Id);
            }
            if (change == "policy-mode") await db.Set<ImageUpdatePolicy>().Where(x => x.Id == f.Policy).ExecuteUpdateAsync(u => u.SetProperty(x => x.Mode, "notify-only"));
            if (change == "policy-disabled") await db.Set<ImageUpdatePolicy>().Where(x => x.Id == f.Policy).ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, false));
            if (change == "run-cancelled") await db.Set<AutomationRun>().Where(x => x.Id == f.Run).ExecuteUpdateAsync(u => u.SetProperty(x => x.CancelRequested, true));
            return true;
        });
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Failed");
        Assert.DoesNotContain(h.Dispatcher.Requests, x => x.Request.Id == queued.Id);
    }

    [Fact]
    public async Task Applied_commit_metadata_remains_usable_after_log_output_retention_clears_response()
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var baseline = await h.RowAsync(f.Applied.Id);
        Assert.NotNull(baseline.ProtectedAppliedMetadata);
        var metadata = h.Protection.CreateProtector("Stanza.Operation.v1", f.Applied.Id.ToString("N")).Unprotect(baseline.ProtectedAppliedMetadata!);
        Assert.Null(JsonSerializer.Deserialize<AgentWorkResponse>(metadata)!.Output);
        await h.InScope(async services =>
        {
            await services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == f.Applied.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedResponse, (string?)null)); return true;
        });
        var queued = await f.EnqueueAsync();
        Assert.Equal(new string('a', 40), h.Decode(await h.RowAsync(queued.Id)).Sources![0].Reference);
    }

    [Fact]
    public async Task Image_only_deployments_need_no_Git_commit_metadata_and_private_definition_metadata_never_enters_the_Agent_request()
    {
        await using var f = await Fixture.CreateAsync(includeGit: false); var h = f.Harness;
        await h.InScope(async services =>
        {
            var response = new AgentWorkResponse(f.Applied.Id, true, "ok");
            var encrypted = h.Protection.CreateProtector("Stanza.Operation.v1", f.Applied.Id.ToString("N")).Protect(JsonSerializer.Serialize(response));
            await services.GetRequiredService<StanzaDbContext>().Set<OperationEntity>().Where(x => x.Id == f.Applied.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedAppliedMetadata, encrypted)); return true;
        });
        var queued = await f.EnqueueAsync(); var row = await h.RowAsync(queued.Id);
        Assert.Equal(f.Applied.Id, row.ParentAppliedOperationId);
        var decoded = h.Decode(row); Assert.Empty(decoded.Sources!);
        Assert.DoesNotContain("_stanzaSourceDefinitions", JsonSerializer.Serialize(decoded));
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Succeeded");
    }

    [Fact]
    public async Task Repeated_automatic_updates_compare_authored_branch_definitions_but_keep_the_applied_commit_pinned()
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var first = await f.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(first.Id, "Succeeded"); await h.StopWorkerAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.NewRunAsync();
        var second = await f.EnqueueAsync(); var row = await h.RowAsync(second.Id);
        Assert.Equal(first.Id, row.ParentAppliedOperationId);
        Assert.Equal(new string('a', 40), h.Decode(row).Sources![0].Reference);
        await h.StartWorkerAsync(); await h.WaitStateAsync(second.Id, "Succeeded");
    }

    [Fact]
    public async Task A_probe_after_automatic_redeploy_uses_authored_definition_metadata_instead_of_comparing_SHA_to_branch()
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var first = await f.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(first.Id, "Succeeded"); await h.StopWorkerAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok",
            JsonSerializer.Serialize(new[] { new AgentImageObservation("app", "registry.example.test/app:latest", "linux/amd64", "sha256:" + new string('a', 64), "sha256:" + new string('b', 64)) })));
        var observations = await h.InScope(services =>
        {
            var probe = new AgentImageDigestProbe(services.GetRequiredService<StanzaDbContext>(), services.GetRequiredService<DeploymentDraftService>(),
                services.GetRequiredService<AccessService>(), services.GetRequiredService<ISourceSnapshotProvider>(), services.GetRequiredService<AgentPolicyService>(),
                h.Protection, services.GetRequiredService<IAgentCommandDispatcher>(), services.GetRequiredService<AuditService>(), services.GetRequiredService<InstanceSettingsService>());
            return probe.ProbeAsync(h.User, h.Deployment, ["app"]);
        });
        Assert.Single(observations);
        Assert.Equal(new string('a', 40), h.Dispatcher.Requests.Last().Request.Sources![0].Reference);
    }

    [Fact]
    public async Task A_different_successfully_applied_operation_invalidates_the_parent_even_with_unchanged_agent_and_revision()
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        var queued = await f.EnqueueAsync();
        // Represent a queue restored after another worker finished a manual redeploy. The redeploy
        // itself goes through the real worker; no successful deployment/runtime state is fabricated.
        await h.SetStateAsync(queued.Id, "HeldForTest");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var manual = await h.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(manual.Id, "Succeeded"); await h.StopWorkerAsync();
        await h.SetStateAsync(queued.Id, "Queued");
        await h.StartWorkerAsync(); await h.WaitStateAsync(queued.Id, "Failed");
        Assert.DoesNotContain(h.Dispatcher.Requests, x => x.Request.Id == queued.Id);
    }

    [Theory]
    [InlineData("invalid-run-id")]
    [InlineData("missing-run")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-target")]
    [InlineData("wrong-operation")]
    public async Task Image_policy_detection_uses_validated_run_identity_instead_of_trusting_trigger_text(string change)
    {
        await using var f = await Fixture.CreateAsync(); var h = f.Harness;
        if (change == "invalid-run-id")
        {
            await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(actor: h.User, trigger: "automation:not-an-id")); return;
        }
        if (change == "missing-run")
        {
            await Assert.ThrowsAsync<OperationException>(() => h.EnqueueAsync(actor: h.User, trigger: "automation:" + Guid.NewGuid().ToString("N"))); return;
        }
        await h.InScope(async services =>
        {
            var runs = services.GetRequiredService<StanzaDbContext>().Set<AutomationRun>().Where(x => x.Id == f.Run);
            if (change == "wrong-owner") await runs.ExecuteUpdateAsync(u => u.SetProperty(x => x.OwnerId, h.Root));
            if (change == "wrong-target") await runs.ExecuteUpdateAsync(u => u.SetProperty(x => x.DeploymentId, Guid.NewGuid()));
            if (change == "wrong-operation") await runs.ExecuteUpdateAsync(u => u.SetProperty(x => x.Operation, "deploy"));
            return true;
        });
        await Assert.ThrowsAsync<OperationException>(f.EnqueueAsync);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public OperationTestHarness Harness { get; private set; } = null!;
        public CredentialSummary Git { get; private set; } = null!;
        public CredentialSummary Registry { get; private set; } = null!;
        public GitSourceSummary Source { get; private set; } = null!;
        public OperationSummary Applied { get; private set; } = null!;
        public Guid Policy { get; private set; }
        public Guid Run { get; private set; }
        private string runSource = "image-policy";
        public static async Task<Fixture> CreateAsync(string source = "image-policy", bool includeGit = true)
        {
            var f = new Fixture { Harness = await OperationTestHarness.CreateAsync(), runSource = source }; var h = f.Harness;
            if (includeGit) { f.Git = await h.GitCredentialAsync(); f.Source = await h.AddSourceAsync(f.Git.Id); }
            f.Registry = await h.RegistryCredentialAsync();
            await h.InScope(async services => { await services.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [f.Registry.Id], []); return true; });
            h.Dispatcher.Handler = (_, request, _) => Task.FromResult(new AgentWorkResponse(request.Id, true, "ok", "[{\"State\":\"running\",\"Health\":\"healthy\"}]",
                request.Sources?.ToDictionary(s => s.Destination, s => s.Reference.Length == 40 && s.Reference.All(Uri.IsHexDigit) ? s.Reference : new string('a', 40))));
            f.Applied = await h.EnqueueAsync(); await h.StartWorkerAsync(); await h.WaitStateAsync(f.Applied.Id, "Succeeded"); await h.StopWorkerAsync();
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await h.AuthorizeUserAsync(); await h.GrantAsync("automation.read", "deployment", h.Deployment); await h.GrantAsync("automation.manage", "deployment", h.Deployment);
            if (includeGit) await h.GrantAsync("credential.use", "credential", f.Git.Id);
            await h.GrantAsync("credential.use", "credential", f.Registry.Id);
            f.Policy = Guid.NewGuid();
            await h.InScope(async services =>
            {
                services.GetRequiredService<StanzaDbContext>().Add(new ImageUpdatePolicy { Id = f.Policy, OwnerId = h.User, DeploymentId = h.Deployment,
                    Name = "Images", Services = "app", Mode = "auto-deploy", Enabled = true, Revision = 1 });
                await services.GetRequiredService<StanzaDbContext>().SaveChangesAsync(); return true;
            });
            await f.NewRunAsync(); return f;
        }
        public Task NewRunAsync() => Harness.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>(); var h = Harness;
            var policy = await db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync(x => x.Id == Policy);
            Run = Guid.NewGuid();
            db.Add(new AutomationRun { Id = Run, OwnerId = h.User, RequestedBy = h.User, DeploymentId = h.Deployment, DefinitionId = Policy,
                Source = runSource, Operation = "image-check", State = "running", OccurrenceKey = Run.ToString("N"),
                CreatedAt = h.Clock.GetUtcNow().ToUnixTimeSeconds(), LeaseUntil = h.Clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds(),
                ProtectedPlan = services.GetRequiredService<AutomationService>().Protect(ImagePolicySnapshot.From(policy)) });
            await db.SaveChangesAsync(); return true;
        });
        public Task<OperationSummary> EnqueueAsync() => Harness.EnqueueAsync(actor: Harness.User, trigger: "automation:" + Run.ToString("N"));
        public Task MutateAsync(string change) => Harness.InScope(async services =>
        {
            var h = Harness; var db = services.GetRequiredService<StanzaDbContext>();
            if (change is "draft" or "deployment-environment")
            {
                await services.GetRequiredService<DeploymentDraftService>().SaveAsync(h.Root, h.Deployment, new("Changed", h.Agent, 1,
                    change == "draft" ? "services: {changed: {image: pending/config}}" : null, change == "deployment-environment" ? "PENDING=secret\n" : null));
            }
            if (change == "placement")
            {
                var target = await h.AddAgentAsync();
                // Invoke the real authorization/revision-aware placement stage used by the guided move,
                // rather than bypassing the public editor's intentional applied-placement restriction.
                var method = typeof(DeploymentDraftService).GetMethod("ReassignPortablePlacementAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                await (Task<DeploymentDraftSummary>)method.Invoke(services.GetRequiredService<DeploymentDraftService>(), [h.Root, h.Deployment, 1L, target, CancellationToken.None])!;
            }
            if (change == "global-environment") await services.GetRequiredService<InstanceSettingsService>().SaveEditorAsync(h.Root, null, "PENDING=global\n", 1);
            if (change == "agent-environment") await services.GetRequiredService<AgentAdministrationService>().SaveEnvironmentAsync(h.Root, h.Agent, new("PENDING=agent\n", 0));
            if (change == "source-reference") await services.GetRequiredService<SourceService>().SaveGitAsync(h.Root, h.Deployment, Source.Id, Source.Version,
                new(Source.Url, "pending-branch", Source.Destination, Source.CredentialId));
            if (change == "source-added") await services.GetRequiredService<SourceService>().SaveGitAsync(h.Root, h.Deployment, null, 0,
                new("https://other.example.test/repo.git", "main", "sources/other", null));
            if (change == "credential-binding")
            {
                var replacement = await h.GitCredentialAsync(); await h.GrantAsync("credential.use", "credential", replacement.Id);
                await services.GetRequiredService<SourceService>().SaveGitAsync(h.Root, h.Deployment, Source.Id, Source.Version,
                    new(Source.Url, Source.Reference, Source.Destination, replacement.Id));
            }
            if (change == "registry-credential-binding")
            {
                var replacement = await h.RegistryCredentialAsync(); await h.GrantAsync("credential.use", "credential", replacement.Id);
                await services.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [replacement.Id], [Registry.Id]);
            }
            if (change == "registry-binding") await services.GetRequiredService<SourceService>().SetRegistriesAsync(h.Root, h.Deployment, [], [Registry.Id]);
            if (change is "pending-operation" or "unknown-operation")
            {
                var other = await h.EnqueueAsync("validate");
                if (change == "unknown-operation") await h.SetStateAsync(other.Id, "Unknown", h.Clock.GetUtcNow().ToUnixTimeSeconds());
            }
            if (change == "runtime-agent")
            {
                var agent = await h.AddAgentAsync();
                await db.Set<DeploymentRuntimeEntity>().Where(x => x.DeploymentId == h.Deployment).ExecuteUpdateAsync(u => u.SetProperty(x => x.AgentId, agent));
            }
            if (change == "missing-metadata") await db.Set<OperationEntity>().Where(x => x.Id == Applied.Id).ExecuteUpdateAsync(u =>
                u.SetProperty(x => x.ProtectedResponse, (string?)null).SetProperty(x => x.ProtectedAppliedMetadata, (string?)null));
            if (change == "invalid-commit")
            {
                var response = new AgentWorkResponse(Applied.Id, true, "ok", ResolvedSources: new Dictionary<string, string> { [Source.Destination] = "not-an-exact-commit" });
                var encrypted = h.Protection.CreateProtector("Stanza.Operation.v1", Applied.Id.ToString("N")).Protect(JsonSerializer.Serialize(response));
                await db.Set<OperationEntity>().Where(x => x.Id == Applied.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedAppliedMetadata, encrypted));
            }
            return true;
        });
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }
}
