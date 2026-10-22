using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Portability;

public sealed class PortabilityTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    private readonly FakeClock clock = new();
    private StanzaDbContext db = null!;
    private AccountService accounts = null!;
    private AccessService access = null!;
    private DeploymentDraftService drafts = null!;
    private IPortableDraftStore store = null!;
    private PortabilityService service = null!;
    private Guid root;
    private Guid user;
    private readonly Guid sourceAgent = Guid.NewGuid();
    private readonly Guid destinationAgent = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        accounts = new(db, clock);
        access = new(db, clock);
        drafts = new(db, protection, accounts, access, clock);
        store = new PortableDraftStore(drafts, access);
        service = Service();
        root = (await accounts.BootstrapAsync("root", "a-secure-password-123")).Id;
        user = (await accounts.CreateAsync(root, "user", "a-secure-password-123", AccountRole.User)).Id;
        db.AddRange(new AgentEntity { Id = sourceAgent, Identity = "source", Hostname = "source", Status = "approved" },
            new AgentEntity { Id = destinationAgent, Identity = "destination", Hostname = "destination", Status = "approved" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task Plaintext_roundtrip_keeps_exact_manual_compose_environment_text_and_binary()
    {
        var id = await Source();
        var bytes = await service.ExportAsync(root, new([id], [], true));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("secret-env", text);
        Assert.Contains("secret-compose", text);
        Assert.Contains("base64", text);
        Assert.DoesNotContain(sourceAgent.ToString(), text);
        var bundle = PortableBundleValidator.Parse(bytes);
        var preview = await service.PreviewAsync(root, bytes, Map(bundle, "Imported"));
        Assert.True(preview.CanApply);
        Assert.Contains(preview.Warnings, x => x.Contains("NOT transferred"));
        var row = await db.Set<PortableImportEntity>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain("secret-env", Encoding.UTF8.GetString(row.ProtectedContent));
        var result = await service.ApplyAsync(root, preview.Id, preview.Fingerprint, true);
        Assert.Equal("complete", result.State);
        var importedId = Assert.Single(result.CreatedDeployments);
        Assert.NotEqual(id, importedId);
        var imported = await drafts.ReadAsync(root, importedId);
        var source = await drafts.ReadAsync(root, id);
        Assert.Equal(source.ComposeText, imported.ComposeText);
        Assert.Equal(source.EnvironmentText, imported.EnvironmentText);
        Assert.Equal(destinationAgent, imported.Draft.TargetAgentId);
        Assert.Equal("FROM scratch\r\n# text-secret\n", (await drafts.ReadFileAsync(root, importedId, "build/Dockerfile")).GetText());
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, (await drafts.ReadFileAsync(root, importedId, "asset.bin")).GetBytes());
        Assert.Equal(Stanza.Core.Deployments.ManagedEntryKind.Directory, (await drafts.ReadFileAsync(root, importedId, "empty-directory")).Kind);
        Assert.Empty((await db.Set<PortableImportEntity>().AsNoTracking().SingleAsync()).ProtectedContent);
    }

    [Fact]
    public async Task Acknowledgement_is_required_before_any_generation_or_application()
    {
        await Assert.ThrowsAsync<PortabilityException>(() => service.ExportAsync(root, new([Guid.NewGuid()], [], false)));
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, Guid.NewGuid(), "fake", false));
        await Assert.ThrowsAsync<PortabilityException>(() => service.CloneAsync(root, Guid.NewGuid(), "Clone", destinationAgent, false));
        Assert.Empty(await db.Set<PortableImportEntity>().ToListAsync());
        Assert.Empty(await db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Export_permission_does_not_bypass_files_environment_or_resource_permissions()
    {
        var id = await Source();
        await access.AddGrantAsync(root, user, null, "configuration.export", "instance", null);
        await access.AddGrantAsync(root, user, null, "deployment.read", "deployment", id);
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ExportAsync(user, new([id], [], true)));
        await access.AddGrantAsync(root, user, null, "files.read", "deployment", id);
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ExportAsync(user, new([id], [], true)));
        await access.AddGrantAsync(root, user, null, "environment.reveal", "deployment", id);
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ExportAsync(user, new([id], [], true)));
        await access.AddGrantAsync(root, user, null, "agent.read", "agent", sourceAgent);
        Assert.NotEmpty(await service.ExportAsync(user, new([id], [], true)));
        var another = await drafts.CreateAsync(root, "Another", null, "", "");
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ExportAsync(user, new([another.Id], [], true)));
    }

    [Fact]
    public async Task Per_deployment_export_grant_is_sufficient_but_not_for_instance_sections()
    {
        var id = await Source();
        foreach (var action in new[] { "configuration.export", "deployment.read", "files.read", "environment.reveal" })
            await access.AddGrantAsync(root, user, null, action, "deployment", id);
        await access.AddGrantAsync(root, user, null, "agent.read", "agent", sourceAgent);
        Assert.NotEmpty(await service.ExportAsync(user, new([id], [], true)));
        var participant = new FakeParticipant();
        await Assert.ThrowsAsync<PortabilityAccessException>(() => Service(participant).ExportAsync(user, new([id], [participant.Kind], true)));
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("credentials")]
    [InlineData("global-environment")]
    [InlineData("groups")]
    [InlineData("agent-templates")]
    public async Task Unsupported_sections_are_reported_not_silently_omitted(string kind)
    {
        var error = await Assert.ThrowsAsync<PortabilityException>(() => service.ExportAsync(root, new([], [kind], true)));
        Assert.Contains("Unsupported portability section", error.Message);
        var bundle = Bundle() with { Sections = [new(kind, JsonSerializer.SerializeToElement(new { }))] };
        await Assert.ThrowsAsync<PortabilityException>(() => service.PreviewAsync(root, Bytes(bundle), Map(bundle, "New")));
    }

    [Fact]
    public async Task Conflicts_and_unmapped_agents_block_apply_without_mutation()
    {
        var id = await Source();
        var bytes = await service.ExportAsync(root, new([id], [], true));
        var bundle = PortableBundleValidator.Parse(bytes);
        var preview = await service.PreviewAsync(root, bytes, new(new Dictionary<string, Guid>(), new Dictionary<string, string>(), new Dictionary<string, string>()));
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Conflicts, x => x.Contains("Map Agent"));
        Assert.Contains(preview.Conflicts, x => x.Contains("name conflict"));
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, preview.Id, preview.Fingerprint, true));
        Assert.Single(await db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Preview_fingerprint_cannot_be_tampered_and_replays_are_rejected()
    {
        var bundle = Bundle();
        var mapping = Map(bundle, "Imported");
        var preview = await service.PreviewAsync(root, Bytes(bundle), mapping);
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, preview.Id, new string('F', 64), true));
        await Assert.ThrowsAsync<PortabilityException>(() => service.PreviewAsync(root, Bytes(bundle), mapping));
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ReadPreviewAsync(user, preview.Id));
        var applied = await service.ApplyAsync(root, preview.Id, preview.Fingerprint, true);
        Assert.Equal("complete", applied.State);
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, preview.Id, preview.Fingerprint, true));
        Assert.Single(await db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Expired_previews_and_ciphertext_swapping_fail_closed()
    {
        var bundle = Bundle();
        var preview = await service.PreviewAsync(root, Bytes(bundle), Map(bundle, "Expired"));
        clock.Now = clock.Now.AddMinutes(21);
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, preview.Id, preview.Fingerprint, true));
        var fresh = await service.PreviewAsync(root, Bytes(bundle), Map(bundle, "Fresh"));
        var old = await db.Set<PortableImportEntity>().AsNoTracking().SingleAsync(x => x.Id == preview.Id);
        await db.Set<PortableImportEntity>().Where(x => x.Id == fresh.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedContent, old.ProtectedContent));
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, fresh.Id, fresh.Fingerprint, true));
    }

    [Fact]
    public async Task Concurrent_application_claims_preview_only_once()
    {
        var bundle = Bundle();
        var blocking = new BlockingStore(store);
        var firstService = new PortabilityService(db, access, drafts, blocking, protection, clock, []);
        var preview = await firstService.PreviewAsync(root, Bytes(bundle), Map(bundle, "OnceOnly"));
        var applying = firstService.ApplyAsync(root, preview.Id, preview.Fingerprint, true);
        await blocking.Entered.Task;
        await Assert.ThrowsAsync<PortabilityException>(() => service.ApplyAsync(root, preview.Id, preview.Fingerprint, true));
        blocking.Proceed.SetResult();
        Assert.Equal("complete", (await applying).State);
        Assert.Single(await db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Agent_mapping_requires_registered_approved_agent_and_both_target_permissions()
    {
        var bundle = Bundle();
        var bad = new PortableImportMapping(new Dictionary<string, Guid> { ["agent"] = Guid.NewGuid() }, new Dictionary<string, string>(), new Dictionary<string, string>());
        await Assert.ThrowsAsync<PortabilityException>(() => service.PreviewAsync(root, Bytes(bundle), bad));
        await db.Set<AgentEntity>().Where(x => x.Id == destinationAgent).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "pending"));
        await Assert.ThrowsAsync<PortabilityException>(() => service.PreviewAsync(root, Bytes(bundle), Map(bundle, "New")));
    }

    [Fact]
    public async Task Clone_has_new_identity_exact_files_no_implicit_grants_or_activation()
    {
        var source = await Source();
        var id = await service.CloneAsync(root, source, "Clone", destinationAgent, true);
        Assert.NotEqual(source, id);
        var clone = await drafts.ReadAsync(root, id);
        Assert.Equal(destinationAgent, clone.Draft.TargetAgentId);
        Assert.Equal((await drafts.ReadAsync(root, source)).ComposeText, clone.ComposeText);
        Assert.Equal((await drafts.ReadAsync(root, source)).EnvironmentText, clone.EnvironmentText);
        Assert.Equal((await drafts.ReadFileAsync(root, source, "asset.bin")).GetBytes(), (await drafts.ReadFileAsync(root, id, "asset.bin")).GetBytes());
        Assert.Empty(await db.Grants.Where(x => x.ResourceId == id).ToListAsync());
        Assert.Empty(await db.Set<PortableMoveEntity>().ToListAsync());
    }

    [Fact]
    public async Task Participant_imports_receive_inactive_flag_and_created_reference_mapping()
    {
        var p = new FakeParticipant();
        var s = Service(p);
        var bundle = Bundle() with { Sections = [new(p.Kind, JsonSerializer.SerializeToElement(new { secret = "visible-token" }))] };
        var preview = await s.PreviewAsync(root, Bytes(bundle), Map(bundle, "Imported"));
        Assert.False(p.Applied);
        var result = await s.ApplyAsync(root, preview.Id, preview.Fingerprint, true);
        Assert.Equal("complete", result.State);
        Assert.True(p.Applied);
        Assert.NotNull(p.Context);
        Assert.False(p.Context!.ActivateAutomation);
        Assert.Equal(Assert.Single(result.CreatedDeployments), p.Context.CreatedDeployments["deployment"]);
    }

    [Fact]
    public async Task Participant_failure_reports_partial_import_preserves_created_ids_and_prevents_replay()
    {
        var p = new FakeParticipant { FailApply = true };
        var s = Service(p);
        var bundle = Bundle() with { Sections = [new(p.Kind, JsonSerializer.SerializeToElement(new { }))] };
        var preview = await s.PreviewAsync(root, Bytes(bundle), Map(bundle, "Partial"));
        var result = await s.ApplyAsync(root, preview.Id, preview.Fingerprint, true);
        Assert.Equal("partial", result.State);
        Assert.Single(result.CreatedDeployments);
        Assert.DoesNotContain("sensitive-exception-text", result.Failure!);
        var reread = await s.ReadResultAsync(root, preview.Id);
        Assert.Equal(result.State, reread.State);
        Assert.Equal(result.CreatedDeployments, reread.CreatedDeployments);
        await Assert.ThrowsAsync<PortabilityException>(() => s.ApplyAsync(root, preview.Id, preview.Fingerprint, true));
    }

    [Fact]
    public async Task Security_sections_require_system_admin_even_if_operational_admin()
    {
        var admin = await accounts.CreateAsync(root, "admin", "a-secure-password-123", AccountRole.Admin);
        var p = new FakeParticipant { IsSecurityPolicy = true };
        await Assert.ThrowsAsync<PortabilityAccessException>(() => Service(p).ExportAsync(admin.Id, new([], [p.Kind], true)));
        var bundle = Bundle() with { Sections = [new(p.Kind, JsonSerializer.SerializeToElement(new { }))] };
        await Assert.ThrowsAsync<PortabilityAccessException>(() => Service(p).PreviewAsync(admin.Id, Bytes(bundle), Map(bundle, "NoEscalation")));
    }

    [Fact]
    public async Task Revoke_permissions_after_preview_is_enforced_at_apply()
    {
        await access.AddGrantAsync(root, user, null, "configuration.import", "instance", null);
        await access.AddGrantAsync(root, user, null, "deployment.create", "instance", null);
        await access.AddGrantAsync(root, user, null, "agent.read", "agent", destinationAgent);
        var manage = await access.AddGrantAsync(root, user, null, "agent.manage", "agent", destinationAgent);
        var bundle = Bundle() with { Deployments = [Bundle().Deployments[0] with { Files = [] }] };
        var preview = await service.PreviewAsync(user, Bytes(bundle), Map(bundle, "UserImport"));
        await access.RemoveGrantAsync(root, manage.Id);
        await Assert.ThrowsAsync<PortabilityAccessException>(() => service.ApplyAsync(user, preview.Id, preview.Fingerprint, true));
        Assert.Empty(await db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Move_is_durable_ordered_and_placement_changes_only_after_proven_destination_health()
    {
        var source = await Source();
        var ops = new FakeOperations();
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        for (var step = 0; step < 5; step++)
        {
            await moves.AdvanceAsync(id);
            Assert.Equal(sourceAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
        }
        Assert.Equal("placement", (await moves.ReadAsync(root, id)).State);
        await moves.AdvanceAsync(id);
        Assert.Equal("complete", (await moves.ReadAsync(root, id)).State);
        var warnings = (await moves.ReadAsync(root, id)).Warnings;
        Assert.Contains("Architecture compatibility is not established", warnings);
        Assert.Contains("not all host ports", warnings);
        Assert.Equal(destinationAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
        Assert.Equal(new[] { "validate", "status", "stop", "deploy", "status" }, ops.Submissions.Select(x => x.Kind));
        Assert.Equal(new[] { destinationAgent, destinationAgent, sourceAgent, destinationAgent, destinationAgent }, ops.Submissions.Select(x => x.AgentId!.Value));
        Assert.All(ops.Submissions, x => Assert.Equal(root, x.ActorId));
        Assert.DoesNotContain(ops.Submissions, x => x.Kind.Contains("remove") || x.Kind.Contains("volume"));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("stop")]
    [InlineData("deploy")]
    [InlineData("status")]
    public async Task Move_failure_keeps_source_placement_and_recovery_state(string failedKind)
    {
        var source = await Source();
        var ops = new FakeOperations { FailedKind = failedKind };
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        for (var i = 0; i < 6; i++) await moves.AdvanceAsync(id);
        var row = await moves.ReadAsync(root, id);
        Assert.Equal("recovery", row.State);
        Assert.NotNull(row.OperationId);
        Assert.Equal(sourceAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
        Assert.DoesNotContain(ops.Submissions, x => x.Kind.Contains("remove"));
        await Assert.ThrowsAsync<PortabilityException>(() => moves.QueueAsync(root, source, destinationAgent, true));
    }

    [Fact]
    public async Task Source_cleanup_is_separate_explicit_authorized_and_targets_only_old_agent()
    {
        var source = await Source();
        var ops = new FakeOperations();
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        for (var i = 0; i < 6; i++) await moves.AdvanceAsync(id);
        Assert.DoesNotContain(ops.Submissions, x => x.Kind == "remove");
        await Assert.ThrowsAsync<PortabilityException>(() => moves.QueueSourceCleanupAsync(root, id, false));
        await Assert.ThrowsAsync<PortabilityAccessException>(() => moves.QueueSourceCleanupAsync(user, id, true));
        await moves.QueueSourceCleanupAsync(root, id, true);
        await Assert.ThrowsAsync<PortabilityException>(() => moves.QueueSourceCleanupAsync(root, id, true));
        await moves.AdvanceAsync(id);
        Assert.Equal("cleaned", (await moves.ReadAsync(root, id)).State);
        var cleanup = Assert.Single(ops.Submissions, x => x.Kind == "remove");
        Assert.Equal(sourceAgent, cleanup.AgentId);
        Assert.Null(cleanup.Execution);
        Assert.Equal(destinationAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
        Assert.NotEmpty((await drafts.ReadAsync(root, source)).Files);
    }

    [Fact]
    public async Task Status_command_success_without_health_evidence_does_not_mark_move_complete()
    {
        var source = await Source();
        var ops = new FakeOperations { Healthy = false };
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        for (var i = 0; i < 5; i++) await moves.AdvanceAsync(id);
        Assert.Equal("recovery", (await moves.ReadAsync(root, id)).State);
        Assert.Equal(sourceAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
    }

    [Fact]
    public async Task Restart_with_known_operation_waits_same_id_and_unknown_submission_never_replays()
    {
        var source = await Source();
        var ops = new FakeOperations();
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        var pending = await ops.EnqueueAsync(new(root, source, "validate", "move", destinationAgent));
        await db.Set<PortableMoveEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OperationId, pending.Id));
        await Moves(ops).AdvanceAsync(id);
        Assert.Single(ops.Submissions);
        Assert.Equal(pending.Id, Assert.Single(ops.Waited));
        await db.Set<PortableMoveEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "submitting-stop-source"));
        await Moves(ops).AdvanceAsync(id);
        Assert.Single(ops.Submissions);
        Assert.Equal("recovery", (await moves.ReadAsync(root, id)).State);
    }

    [Fact]
    public async Task Move_rechecks_authorization_and_saved_revision_before_side_effects()
    {
        var source = await Source();
        var ops = new FakeOperations();
        var moves = Moves(ops);
        await Assert.ThrowsAsync<PortabilityException>(() => moves.QueueAsync(root, source, destinationAgent, false));
        await Assert.ThrowsAsync<PortabilityAccessException>(() => moves.QueueAsync(user, source, destinationAgent, true));
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        var draft = await drafts.ReadAsync(root, source);
        await drafts.SaveAsync(root, source, new("Changed", sourceAgent, draft.ViewedRevision, null, null));
        await moves.AdvanceAsync(id);
        Assert.Equal("recovery", (await moves.ReadAsync(root, id)).State);
        Assert.Empty(ops.Submissions);
    }

    [Fact]
    public async Task Revoked_destination_approval_pauses_move_before_stopping_source()
    {
        var source = await Source(); var ops = new FakeOperations(); var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        await db.Set<AgentEntity>().Where(x => x.Id == destinationAgent).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "revoked"));
        await moves.AdvanceAsync(id);
        Assert.Equal("recovery", (await moves.ReadAsync(root, id)).State);
        Assert.Empty(ops.Submissions);
        Assert.Equal(sourceAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
    }

    [Fact]
    public async Task Placement_commit_rechecks_move_permission_under_the_same_guard_as_revision_commit()
    {
        var source = await Source();
        foreach (var action in new[] { "deployment.read", "files.write" })
            await access.AddGrantAsync(root, user, null, action, "deployment", source);
        foreach (var action in new[] { "agent.read", "agent.manage" })
            await access.AddGrantAsync(root, user, null, action, "agent", destinationAgent);
        var move = await access.AddGrantAsync(root, user, null, "deployment.move", "deployment", source);
        var before = await drafts.ReadAsync(root, source);
        await access.RemoveGrantAsync(root, move.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => store.ReassignAsync(user, source, before.ViewedRevision, destinationAgent, CancellationToken.None));
        var after = await drafts.ReadAsync(root, source);
        Assert.Equal(before.ViewedRevision, after.ViewedRevision);
        Assert.Equal(sourceAgent, after.Draft.TargetAgentId);
    }

    [Fact]
    public async Task Recovery_closure_requires_explicit_ack_and_fresh_correlated_inspections_of_both_agents_without_changing_placement()
    {
        var source = await Source();
        var ops = new FakeOperations { FailedKind = "stop" };
        var moves = Moves(ops);
        var id = await moves.QueueAsync(root, source, destinationAgent, true);
        await moves.AdvanceAsync(id);
        await moves.AdvanceAsync(id);
        await moves.AdvanceAsync(id);
        await Assert.ThrowsAsync<PortabilityException>(() => moves.CloseRecoveryAsync(root, id, false));
        await Assert.ThrowsAsync<PortabilityException>(() => moves.CloseRecoveryAsync(root, id, true));
        var sourceStatus = await moves.QueueRecoveryInspectionAsync(root, id, false);
        var destinationStatus = await moves.QueueRecoveryInspectionAsync(root, id, true);
        Assert.Equal(sourceAgent, ops.Submissions[^2].AgentId);
        Assert.Equal(destinationAgent, ops.Submissions[^1].AgentId);
        var sourceRow = new OperationEntity { Id = sourceStatus, ActorId = root, DeploymentId = source, AgentId = sourceAgent,
            Kind = "status", State = "Succeeded", Trigger = "move-recovery:" + id.ToString("N"), CreatedAt = clock.Now.ToUnixTimeSeconds() };
        db.Add(sourceRow);
        await db.SaveChangesAsync();
        db.Entry(sourceRow).State = EntityState.Detached;
        await Assert.ThrowsAsync<PortabilityException>(() => moves.CloseRecoveryAsync(root, id, true));
        var destinationRow = new OperationEntity { Id = destinationStatus, ActorId = root, DeploymentId = source, AgentId = destinationAgent,
            Kind = "status", State = "Succeeded", Trigger = "move-recovery:" + id.ToString("N"), CreatedAt = clock.Now.ToUnixTimeSeconds() };
        db.Add(destinationRow);
        await db.SaveChangesAsync();
        db.Entry(destinationRow).State = EntityState.Detached;
        await moves.CloseRecoveryAsync(root, id, true);
        Assert.Equal("cancelled", (await moves.ReadAsync(root, id)).State);
        Assert.Equal(sourceAgent, (await drafts.ReadAsync(root, source)).Draft.TargetAgentId);
        Assert.Equal(new[] { "validate", "status", "stop", "status", "status" }, ops.Submissions.Select(x => x.Kind));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/escape")]
    [InlineData("dir\\file")]
    [InlineData("compose.yaml")]
    [InlineData(".env")]
    [InlineData("NUL.txt")]
    public void Bundle_rejects_traversal_reserved_paths_and_unsafe_files(string path)
    {
        var b = Bundle();
        b = b with { Deployments = [b.Deployments[0] with { Files = [new(path, "text", "data", null)] }] };
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b)));
    }

    [Fact]
    public void Bundle_rejects_unsupported_schema_references_file_kinds_collisions_and_limits()
    {
        var b = Bundle();
        var d = b.Deployments[0];
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Version = 2 })));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Agents = [] })));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Deployments = [d, d] })));
        foreach (var file in new[] { new PortableFile("link", "symlink", "target", null), new("bad.bin", "binary", null, "!!!!"), new("both", "text", "text", "AA==") })
            Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Deployments = [d with { Files = [file] }] })));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Deployments = [d with { Files = [new("Dir/file", "text", "", null), new("dir/other", "text", "", null)] }] })));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(new byte[PortableBundleValidator.MaximumBundleBytes + 1]));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Bytes(b with { Deployments = [d with { Compose = new string('x', DeploymentDraftService.MaximumTextBytes + 1) }] })));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"version\":1,\"deployments\":[],\"agents\":[],\"sections\":[]}")));
        Assert.Throws<PortabilityException>(() => PortableBundleValidator.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"deployments\":[],\"agents\":[],\"sections\":[],\"unexpected\":true}")));
    }

    [Fact]
    public void Conservative_storage_warnings_never_claim_a_clean_conflict_free_destination()
    {
        var warnings = PortableBundleValidator.Warnings("services: {}\n");
        Assert.Contains(warnings, x => x.Contains("does not prove absence"));
        Assert.Contains(warnings, x => x.Contains("NOT transferred"));
        Assert.True(PortableBundleValidator.Warnings("services:\n  app:\n    ports: ['8080:80']\n    volumes: ['./data:/data']").Count > warnings.Count);
    }

    [Fact]
    public void Portable_secret_bearing_models_redact_debug_strings_but_keep_values_in_explicit_plaintext_json()
    {
        var b = Bundle() with { Sections = [new("test", JsonSerializer.SerializeToElement(new { token = "secret-token" }))] };
        Assert.DoesNotContain("SECRET=value", b.Deployments[0].ToString());
        Assert.DoesNotContain("exact", b.Deployments[0].Files[0].ToString());
        Assert.DoesNotContain("secret-token", b.Sections[0].ToString());
        var json = Encoding.UTF8.GetString(Bytes(b));
        Assert.Contains("SECRET=value", json);
        Assert.Contains("secret-token", json);
    }

    [Theory]
    [InlineData("[{\"Service\":\"app\",\"State\":\"running\",\"Health\":\"healthy\"}]", true)]
    [InlineData("[{\"Service\":\"app\",\"State\":\"running\",\"Health\":\"\"}]", true)]
    [InlineData("{\"Service\":\"app\",\"State\":\"running\",\"Health\":\"healthy\"}\n{\"Service\":\"db\",\"State\":\"running\",\"Health\":\"healthy\"}", true)]
    [InlineData("[]", false)]
    [InlineData("", false)]
    [InlineData("success", false)]
    [InlineData("[{\"Service\":\"app\",\"State\":\"exited\",\"Health\":\"healthy\"}]", false)]
    [InlineData("[{\"Service\":\"app\",\"State\":\"running\",\"Health\":\"unhealthy\"}]", false)]
    [InlineData("[{\"Service\":\"app\",\"State\":\"running\",\"Health\":\"starting\"}]", false)]
    [InlineData("[{\"Service\":\"app\",\"State\":\"running\"}]", false)]
    [InlineData("[{\"State\":\"running\",\"Health\":\"healthy\"}]", false)]
    public void Health_requires_actual_running_container_status_and_never_accepts_unknown_results(string output, bool healthy) =>
        Assert.Equal(healthy, PortableMoveHealthVerifier.RuntimeStatusHealthy(output));

    [Fact]
    public async Task Destination_preview_uses_destination_inheritance_and_provenance_without_rewriting_files()
    {
        var source = await Source();
        var settings = new InstanceSettingsService(db, protection);
        await settings.SaveAsync(root, new("", "A=global\nB=global\n", 0));
        var encryptedAgent = protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(destinationAgent.ToString("N")).Protect("B=destination\nC=agent\n");
        await db.Set<AgentEntity>().Where(x => x.Id == destinationAgent).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedEnvironment, encryptedAgent));
        var original = await drafts.ReadAsync(root, source);
        await drafts.SaveAsync(root, source, new(original.Draft.Name, sourceAgent, original.ViewedRevision, null, "C=deployment\r\n"));
        var previewer = new PortableDestinationPreviewService(db, access, settings, protection, drafts, store, service);
        var preview = await previewer.ReadAsync(root, source, destinationAgent, "Clone");
        Assert.NotNull(preview.Environment);
        Assert.True(preview.Environment!.IsSupported);
        Assert.Equal("global", preview.Environment.Values["A"].Value);
        Assert.Equal("destination", preview.Environment.Values["B"].Value);
        Assert.Equal("deployment", preview.Environment.Values["C"].Value);
        Assert.Equal(Stanza.Core.Deployments.EnvironmentScope.Deployment, preview.Environment.Values["C"].Source);
        Assert.Equal("C=deployment\r\n", (await drafts.ReadAsync(root, source)).EnvironmentText);
        Assert.DoesNotContain("secret-compose", preview.ToString());
    }

    [Fact]
    public async Task Destination_preview_does_not_reveal_inherited_values_without_each_scope_permission()
    {
        var source = await Source();
        foreach (var action in new[] { "deployment.read", "files.read", "environment.reveal" })
            await access.AddGrantAsync(root, user, null, action, "deployment", source);
        foreach (var action in new[] { "agent.read", "agent.manage" })
            await access.AddGrantAsync(root, user, null, action, "agent", destinationAgent);
        var settings = new InstanceSettingsService(db, protection);
        var preview = await new PortableDestinationPreviewService(db, access, settings, protection, drafts, store, service)
            .ReadAsync(user, source, destinationAgent, "Clone");
        Assert.Null(preview.Environment);
        Assert.Contains("hidden", preview.EnvironmentUnavailable!);
    }

    [Fact]
    public async Task Destination_preview_refuses_to_approximate_compose_environment_syntax()
    {
        var source = await Source();
        var settings = new InstanceSettingsService(db, protection);
        await settings.SaveAsync(root, new("", "QUOTED='not-a-literal'\n", 0));
        var preview = await new PortableDestinationPreviewService(db, access, settings, protection, drafts, store, service)
            .ReadAsync(root, source, destinationAgent, "Clone");
        Assert.NotNull(preview.Environment);
        Assert.False(preview.Environment!.IsSupported);
        Assert.Empty(preview.Environment.Values);
        Assert.Contains("no effective values were approximated", preview.EnvironmentUnavailable!);
    }

    private PortabilityService Service(params IPortableConfigurationParticipant[] p) => new(db, access, drafts, store, protection, clock, p);
    private PortableMoveService Moves(FakeOperations ops) => new(db, drafts, service, store, ops, new PortableMoveHealthVerifier(), clock);
    private async Task<Guid> Source()
    {
        var created = await drafts.CreateAsync(root, "Source", sourceAgent, "# secret-compose\r\nservices: {}\n", "TOKEN=secret-env\r\n");
        await drafts.SaveTextFileAsync(root, created.Id, 1, "build/Dockerfile", "FROM scratch\r\n# text-secret\n", true);
        await drafts.UploadAsync(root, created.Id, 2, "asset.bin", [0, 255, 13, 10, 128]);
        await drafts.CreateDirectoryAsync(root, created.Id, 3, "empty-directory");
        return created.Id;
    }
    private static PortableBundle Bundle() => new(1,
        [new("deployment", "Original", "agent", "services: {}\r\n", "SECRET=value\r\n", [new("file.txt", "text", "exact\r\n", null)])],
        [new("agent", "Logical source")], []);
    private PortableImportMapping Map(PortableBundle b, string name) => new(
        b.Agents.ToDictionary(a => a.Id, _ => destinationAgent), b.Deployments.ToDictionary(d => d.Id, _ => name), new Dictionary<string, string>());
    private static byte[] Bytes(PortableBundle bundle) => JsonSerializer.SerializeToUtf8Bytes(bundle, PortableBundleValidator.JsonOptions);
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class BlockingStore(IPortableDraftStore inner) : IPortableDraftStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PortableDeployment> ReadAsync(Guid actorId, Guid deploymentId, string logicalId, string? logicalAgent, CancellationToken ct) => inner.ReadAsync(actorId, deploymentId, logicalId, logicalAgent, ct);
        public async Task<Guid> CreateAsync(Guid actorId, PortableDeployment deployment, string name, Guid? agentId, CancellationToken ct)
        {
            Entered.SetResult();
            await Proceed.Task.WaitAsync(ct);
            return await inner.CreateAsync(actorId, deployment, name, agentId, ct);
        }
        public Task ReassignAsync(Guid actorId, Guid deploymentId, long expectedRevision, Guid agentId, CancellationToken ct) => inner.ReassignAsync(actorId, deploymentId, expectedRevision, agentId, ct);
    }
    private sealed class FakeParticipant : IPortableConfigurationParticipant
    {
        public string Kind => "test-automation";
        public bool IsSecurityPolicy { get; init; }
        public bool SecurityPolicy => IsSecurityPolicy;
        public bool Applied { get; private set; }
        public bool FailApply { get; init; }
        public PortableParticipantImportContext? Context { get; private set; }
        public Task<JsonElement> ExportAsync(PortableParticipantContext context, CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { secret = "plaintext-token" }));
        public Task<IReadOnlyList<string>> ValidateAsync(JsonElement content, PortableParticipantImportContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task ApplyAsync(JsonElement content, PortableParticipantImportContext context, CancellationToken ct)
        {
            if (FailApply) throw new InvalidOperationException("sensitive-exception-text");
            Applied = true;
            Context = context;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeOperations : IOperationService
    {
        public List<OperationSubmission> Submissions { get; } = [];
        public List<Guid> Waited { get; } = [];
        public string? FailedKind { get; init; }
        public bool Healthy { get; init; } = true;
        private readonly Dictionary<Guid, OperationSummary> results = [];
        public Task<OperationSummary> EnqueueAsync(OperationSubmission submission, CancellationToken token = default)
        {
            Submissions.Add(submission);
            var result = new OperationSummary(Guid.NewGuid(), submission.ActorId, submission.DeploymentId, submission.AgentId!.Value,
                submission.Kind, submission.Kind == FailedKind ? "failed" : "succeeded", submission.Trigger, 0, 1,
                submission.Kind == "status" && Healthy ? "healthy" : "completed");
            results[result.Id] = result;
            return Task.FromResult(result);
        }
        public Task<OperationSummary> WaitAsync(Guid actorId, Guid operationId, CancellationToken token = default)
        { Waited.Add(operationId); return Task.FromResult(results[operationId]); }
        public Task CancelAsync(Guid actorId, Guid operationId, CancellationToken token = default) => Task.CompletedTask;
    }
}
