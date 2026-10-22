using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Portability.Participants;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;

namespace Stanza.Tests.Portability;

public sealed class ParticipantIntegrationTests
{
    [Fact]
    public async Task All_configuration_sections_roundtrip_between_instances_with_plaintext_secrets_logical_references_and_inactive_automation()
    {
        await using var source = await Instance.Create("source-admin");
        await using var destination = await Instance.Create("destination-admin");
        var seeded = await Seed(source);
        var export = await source.Portability.ExportAsync(source.Root, new([seeded.Deployment], source.Participants.Select(p => p.Kind).ToArray(), true));
        var text = Encoding.UTF8.GetString(export);
        foreach (var secret in new[] { "git-password-secret", "registry-password-secret", "global-secret", "agent-secret", seeded.WebhookSecret }) Assert.Contains(secret, text);
        foreach (var id in new[] { source.Root, source.Agent, seeded.Deployment, seeded.Credential, seeded.Action }) Assert.DoesNotContain(id.ToString(), text);
        Assert.DoesNotContain("ProtectedSecret", text);
        Assert.DoesNotContain("PasswordHash", text);
        Assert.DoesNotContain("source-private-identity", text);
        var bundle = PortableBundleValidator.Parse(export);
        Assert.Equal(7, bundle.Sections.Count);
        var mapping = new PortableImportMapping(bundle.Agents.ToDictionary(a => a.Id, _ => destination.Agent),
            new Dictionary<string, string> { [bundle.Deployments[0].Id] = "Imported deployment" },
            new Dictionary<string, string> { ["account:source-admin"] = "destination-admin" });
        var preview = await destination.Portability.PreviewAsync(destination.Root, export, mapping);
        Assert.True(preview.CanApply, string.Join("; ", preview.Conflicts));
        var result = await destination.Portability.ApplyAsync(destination.Root, preview.Id, preview.Fingerprint, true);
        Assert.Equal("complete", result.State);
        var deployment = Assert.Single(result.CreatedDeployments);
        var draft = await destination.Drafts.ReadAsync(destination.Root, deployment);
        Assert.Equal(1, draft.ViewedRevision); // Entire workspace is one atomic initial revision.
        Assert.Equal("# exact compose\r\nservices: {}\n", draft.ComposeText);
        Assert.Equal("DEPLOYMENT=deployment-secret\r\n", draft.EnvironmentText);
        Assert.Equal(new byte[] { 0, 255, 10, 13 }, (await destination.Drafts.ReadFileAsync(destination.Root, deployment, "assets/binary.dat")).GetBytes());
        Assert.Equal("FROM scratch\r\n", (await destination.Drafts.ReadFileAsync(destination.Root, deployment, "build/Dockerfile")).GetText());
        Assert.Equal(Stanza.Core.Deployments.ManagedEntryKind.Directory, (await destination.Drafts.ReadFileAsync(destination.Root, deployment, "empty")).Kind);
        var bindings = await destination.Sources.ReadAsync(destination.Root, deployment);
        var git = Assert.Single(bindings.Git);
        Assert.Equal("checkouts/application", git.Destination);
        Assert.NotEqual(seeded.Credential, git.CredentialId);
        Assert.Equal("git-password-secret", (await destination.Sources.RevealAsync(destination.Root, git.CredentialId!.Value)).Password);
        var registry = Assert.Single(bindings.RegistryCredentialIds);
        Assert.Equal("registry-password-secret", (await destination.Sources.RevealAsync(destination.Root, registry)).Password);
        Assert.Equal("GLOBAL=global-secret\r\n", (await destination.Settings.ReadAsync(destination.Root)).GlobalEnvironment);
        Assert.Equal("https://control.example.test/", (await destination.Settings.ReadAsync(destination.Root)).PublicUrl);
        Assert.Equal("AGENT=agent-secret\r\n", (await destination.Agents.ReadEnvironmentAsync(destination.Root, destination.Agent)).Text);
        Assert.Equal(17, (await destination.Policy.GetAsync()).Policy.OperationTimeoutSeconds);
        var automation = await destination.Automation.ListAsync(destination.Root);
        var action = Assert.Single(automation.Actions);
        Assert.False(action.Enabled);
        Assert.Equal(destination.Root, action.OwnerId);
        Assert.NotEqual(seeded.Action, action.Id);
        Assert.Equal(deployment, Assert.Single((await destination.Automation.ReadActionAsync(destination.Root, action.Id)).Plan.Steps).DeploymentId);
        var schedule = Assert.Single(automation.Schedules);
        Assert.False(schedule.Enabled); Assert.Equal(action.Id, schedule.ActionId); Assert.Equal(destination.Root, schedule.OwnerId);
        var hook = Assert.Single(automation.Webhooks);
        Assert.False(hook.Enabled); Assert.Equal(action.Id, hook.ActionId);
        Assert.Equal(seeded.WebhookSecret, destination.Automation.Unprotect<string>(hook.ProtectedSecret));
        Assert.False(Assert.Single(automation.ImagePolicies).Enabled);
        var group = Assert.Single(await destination.Access.ListGroupsAsync());
        Assert.Contains(destination.Operator, group.Members);
        Assert.Contains(group.Grants, g => g.Action == "deployment.read" && g.ResourceId == deployment);
        Assert.Contains(group.Grants, g => g.Action == "credential.use" && g.ResourceId == git.CredentialId);
        Assert.Contains(group.Grants, g => g.Action == "action.execute" && g.ResourceId == action.Id);
        Assert.Contains(group.Grants, g => g.Action == "agent.read" && g.ResourceId == destination.Agent);
        Assert.True(await destination.Access.IsAllowedAsync(destination.Operator, "environment.reveal", "deployment", deployment));
        Assert.Equal(AccountRole.User, (await destination.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == destination.Operator)).Role);
        Assert.Equal(2, await destination.Db.Accounts.CountAsync());
        Assert.Empty(await destination.Db.Set<EnrollmentTokenEntity>().ToListAsync());
        Assert.Empty(await destination.Db.Set<AutomationRun>().ToListAsync());
        Assert.Empty(await destination.Db.Set<Stanza.ControlPlane.Operations.OperationEntity>().ToListAsync());
    }

    [Fact]
    public async Task Atomic_workspace_creation_needs_import_and_create_but_not_new_identity_read_or_files_write_and_never_grants_access()
    {
        await using var instance = await Instance.Create("root");
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "configuration.import", "instance", null);
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "deployment.create", "instance", null);
        var deployment = new PortableDeployment("deployment", "Atomic", null, "# exact\r\nservices: {}\n", "TOKEN=secret\r\n",
            [new("file", "text", "exact\r\n", null), new("binary", "binary", null, "AP8K"), new("empty", "directory", null, null)]);
        var id = await instance.Store.CreateAsync(instance.Operator, deployment, "Atomic", null, CancellationToken.None);
        var row = await instance.Db.Set<DeploymentDraftEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(id, row.Id); Assert.Equal(1, row.Revision);
        Assert.Single(await instance.Db.Set<DeploymentDraftRevisionEntity>().ToListAsync());
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => instance.Drafts.ReadAsync(instance.Operator, id));
        Assert.Empty(await instance.Db.Grants.Where(g => g.ResourceId == id).ToListAsync());
        Assert.Equal(3, (await instance.Drafts.ReadAsync(instance.Root, id)).Files.Count);
        Assert.DoesNotContain("TOKEN=secret", Encoding.UTF8.GetString((await instance.Db.Set<DeploymentDraftRevisionEntity>().SingleAsync()).ProtectedContent));
    }

    [Fact]
    public async Task Invalid_complete_workspace_fails_before_any_metadata_or_revision_is_saved()
    {
        await using var instance = await Instance.Create("root");
        var deployment = new PortableDeployment("deployment", "Bad", null, "services: {}", "",
            [new("Dir/file", "text", "one", null), new("dir/file", "text", "two", null)]);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => instance.Store.CreateAsync(instance.Root, deployment, "Bad", null, CancellationToken.None));
        Assert.Empty(await instance.Db.Set<DeploymentDraftEntity>().ToListAsync());
        Assert.Empty(await instance.Db.Set<DeploymentDraftRevisionEntity>().ToListAsync());
        deployment = deployment with { Files = [new("build/file", "text", "text", null)], SourceDestinations = ["build"] };
        await Assert.ThrowsAsync<DeploymentDraftException>(() => instance.Store.CreateAsync(instance.Root, deployment, "Bad source collision", null, CancellationToken.None));
        Assert.Empty(await instance.Db.Set<DeploymentDraftEntity>().ToListAsync());
    }

    [Fact]
    public async Task Concrete_source_and_automation_clone_creates_fresh_credentials_and_disabled_definitions_without_changing_instance_settings()
    {
        await using var x = await Instance.Create("root");
        var seeded = await Seed(x);
        var before = await x.Settings.ReadAsync(x.Root);
        var clone = await x.Portability.CloneAsync(x.Root, seeded.Deployment, "Concrete clone", x.Agent, true, ["sources", "automation"]);
        var draft = await x.Drafts.ReadAsync(x.Root, clone);
        Assert.Equal(1, draft.ViewedRevision);
        var sources = await x.Sources.ReadAsync(x.Root, clone);
        Assert.NotEqual(seeded.Credential, Assert.Single(sources.Git).CredentialId);
        Assert.Equal("git-password-secret", (await x.Sources.RevealAsync(x.Root, sources.Git[0].CredentialId!.Value)).Password);
        var automation = await x.Automation.ListAsync(x.Root);
        var schedule = Assert.Single(automation.Schedules, s => s.DeploymentId == clone);
        Assert.False(schedule.Enabled);
        var action = Assert.Single(automation.Actions, a => a.Id == schedule.ActionId);
        Assert.False(action.Enabled); Assert.Equal(x.Root, action.OwnerId);
        Assert.Equal(clone, Assert.Single((await x.Automation.ReadActionAsync(x.Root, action.Id)).Plan.Steps).DeploymentId);
        Assert.Equal(before, await x.Settings.ReadAsync(x.Root));
        Assert.Empty(await x.Db.Grants.Where(g => g.ResourceId == clone).ToListAsync());
        await Assert.ThrowsAsync<PortabilityException>(() => x.Portability.CloneAsync(x.Root, seeded.Deployment, "Not an instance import", x.Agent, true, ["environment"]));
    }

    [Fact]
    public async Task Export_never_uses_credential_use_as_secret_reveal_or_chooses_hidden_credential_metadata()
    {
        await using var instance = await Instance.Create("root");
        var seeded = await Seed(instance);
        foreach (var action in new[] { "deployment.read", "files.read" })
            await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, action, "deployment", seeded.Deployment);
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "configuration.export", "instance", null);
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "agent.read", "agent", instance.Agent);
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "credential.use", "credential", null);
        await Assert.ThrowsAsync<SourceAccessException>(() => instance.Portability.ExportAsync(instance.Operator, new([seeded.Deployment], ["sources"], true)));
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "credential.read", "credential", null);
        await Assert.ThrowsAsync<SourceAccessException>(() => instance.Portability.ExportAsync(instance.Operator, new([seeded.Deployment], ["sources"], true)));
        await instance.Access.AddGrantAsync(instance.Root, instance.Operator, null, "credential.reveal", "credential", null);
        var bytes = await instance.Portability.ExportAsync(instance.Operator, new([seeded.Deployment], ["sources"], true));
        Assert.Contains("git-password-secret", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Unknown_owner_and_git_workspace_collisions_are_rejected_before_creation()
    {
        await using var source = await Instance.Create("source-admin");
        await using var destination = await Instance.Create("destination-admin");
        var seeded = await Seed(source);
        var bytes = await source.Portability.ExportAsync(source.Root, new([seeded.Deployment], ["sources", "automation"], true));
        var bundle = PortableBundleValidator.Parse(bytes);
        var mapping = new PortableImportMapping(bundle.Agents.ToDictionary(a => a.Id, _ => destination.Agent), new Dictionary<string, string>(), new Dictionary<string, string>());
        var error = await Assert.ThrowsAsync<PortabilityException>(() => destination.Portability.PreviewAsync(destination.Root, bytes, mapping));
        Assert.Contains("Existing account mapping required", error.Message);
        var sources = ParticipantSupport.Read<PortableSources>(bundle.Sections.Single(s => s.Kind == "sources").Content);
        var collision = sources with { Bindings = [sources.Bindings[0] with { Git = [sources.Bindings[0].Git[0] with { Destination = "build" }] }] };
        bundle = bundle with { Sections = [new("sources", ParticipantSupport.Json(collision))] };
        await Assert.ThrowsAsync<PortabilityException>(() => destination.Portability.PreviewAsync(destination.Root,
            JsonSerializer.SerializeToUtf8Bytes(bundle, PortableBundleValidator.JsonOptions), mapping));
        Assert.Empty(await destination.Db.Set<DeploymentDraftEntity>().ToListAsync());
        Assert.Empty(await destination.Db.Set<SourceCredentialEntity>().ToListAsync());
    }

    [Fact]
    public async Task Ordinary_import_cannot_import_security_policy_promote_accounts_or_map_group_members_to_admins()
    {
        await using var source = await Instance.Create("source-admin");
        await using var destination = await Instance.Create("destination-admin");
        var seeded = await Seed(source);
        var bytes = await source.Portability.ExportAsync(source.Root, new([seeded.Deployment], ["groups-grants"], true));
        var bundle = PortableBundleValidator.Parse(bytes);
        var mapping = new PortableImportMapping(bundle.Agents.ToDictionary(a => a.Id, _ => destination.Agent), new Dictionary<string, string>(),
            new Dictionary<string, string> { ["account:operator"] = "destination-admin" });
        var admin = await destination.Accounts.CreateAsync(destination.Root, "ordinary-admin", "a-secure-password-123", AccountRole.Admin);
        await Assert.ThrowsAsync<PortabilityAccessException>(() => destination.Portability.PreviewAsync(admin.Id, bytes, mapping));
        await Assert.ThrowsAsync<PortabilityException>(() => destination.Portability.PreviewAsync(destination.Root, bytes, mapping));
        Assert.Empty(await destination.Access.ListGroupsAsync());
        Assert.Equal(AccountRole.User, (await destination.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == destination.Operator)).Role);
    }

    private static async Task<(Guid Deployment, Guid Credential, Guid Action, string WebhookSecret)> Seed(Instance x)
    {
        var deployment = await x.Drafts.CreateAsync(x.Root, "Source deployment", x.Agent, "# exact compose\r\nservices: {}\n", "DEPLOYMENT=deployment-secret\r\n");
        await x.Drafts.SaveTextFileAsync(x.Root, deployment.Id, 1, "build/Dockerfile", "FROM scratch\r\n", true);
        await x.Drafts.UploadAsync(x.Root, deployment.Id, 2, "assets/binary.dat", [0, 255, 10, 13]);
        await x.Drafts.CreateDirectoryAsync(x.Root, deployment.Id, 3, "empty");
        var git = await x.Sources.SaveCredentialAsync(x.Root, null, 0, new("Git", "git", "https://git.example.test/team/app.git", "git-user", "git-password-secret"));
        var registry = await x.Sources.SaveCredentialAsync(x.Root, null, 0, new("Registry", "registry", "registry.example.test", "registry-user", "registry-password-secret"));
        await x.Sources.SaveGitAsync(x.Root, deployment.Id, null, 0, new(git.Endpoint, "main", "checkouts/application", git.Id));
        await x.Sources.SetRegistriesAsync(x.Root, deployment.Id, [registry.Id], []);
        await x.Settings.SaveAsync(x.Root, new("https://control.example.test/", "GLOBAL=global-secret\r\n", 0));
        await x.Agents.SaveEnvironmentAsync(x.Root, x.Agent, new("AGENT=agent-secret\r\n", 0));
        await x.Policy.SaveAsync(x.Root, new(new() { OperationTimeoutSeconds = 17 }, 0));
        var action = await x.Automation.SaveActionAsync(x.Root, null, 0, "Reusable action", true, "queue", new([new("deploy", deployment.Id)]));
        await x.Automation.SaveScheduleAsync(x.Root, new() { DeploymentId = deployment.Id, ActionId = action.Id, Name = "Schedule", Enabled = true }, 0);
        var webhook = await x.Automation.SaveWebhookAsync(x.Root, new() { DeploymentId = deployment.Id, ActionId = action.Id, Name = "Webhook", Enabled = true }, 0);
        await x.Automation.SaveImagePolicyAsync(x.Root, new() { DeploymentId = deployment.Id, Name = "Image policy", Services = "app", Mode = "auto-deploy", Enabled = true }, 0);
        var group = await x.Access.CreateGroupAsync(x.Root, "Operators");
        await x.Access.AddMemberAsync(x.Root, group.Id, x.Operator);
        foreach (var grant in new[] { ("deployment.read", "deployment", deployment.Id), ("credential.use", "credential", git.Id), ("action.execute", "action", action.Id), ("agent.read", "agent", x.Agent) })
            await x.Access.AddGrantAsync(x.Root, null, group.Id, grant.Item1, grant.Item2, grant.Item3);
        await x.Access.AddGrantAsync(x.Root, x.Operator, null, "environment.reveal", "deployment", deployment.Id);
        return (deployment.Id, git.Id, action.Id, webhook.Secret!);
    }

    private sealed class Instance : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public StanzaDbContext Db { get; private set; } = null!;
        public AccountService Accounts { get; private set; } = null!;
        public AccessService Access { get; private set; } = null!;
        public DeploymentDraftService Drafts { get; private set; } = null!;
        public IPortableDraftStore Store { get; private set; } = null!;
        public SourceService Sources { get; private set; } = null!;
        public InstanceSettingsService Settings { get; private set; } = null!;
        public AgentAdministrationService Agents { get; private set; } = null!;
        public OperationalPolicyService Policy { get; private set; } = null!;
        public AutomationService Automation { get; private set; } = null!;
        public IPortableConfigurationParticipant[] Participants { get; private set; } = [];
        public PortabilityService Portability { get; private set; } = null!;
        public Guid Root { get; private set; }
        public Guid Operator { get; private set; }
        public Guid Agent { get; } = Guid.NewGuid();
        public static async Task<Instance> Create(string rootName)
        {
            var x = new Instance(); await x.connection.OpenAsync();
            x.Db = new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(x.connection).Options);
            await x.Db.Database.EnsureCreatedAsync();
            var protection = new EphemeralDataProtectionProvider();
            x.Accounts = new(x.Db, TimeProvider.System); x.Access = new(x.Db, TimeProvider.System);
            x.Root = (await x.Accounts.BootstrapAsync(rootName, "a-secure-password-123")).Id;
            x.Operator = (await x.Accounts.CreateAsync(x.Root, "operator", "a-secure-password-123", AccountRole.User)).Id;
            x.Drafts = new(x.Db, protection, x.Accounts, x.Access, TimeProvider.System);
            x.Store = new PortableDraftStore(x.Drafts, x.Access);
            x.Sources = new(x.Db, x.Access, protection, new NoDispatch());
            x.Settings = new(x.Db, protection); x.Agents = new(x.Db, protection, TimeProvider.System); x.Policy = new(x.Db, x.Access);
            var authorization = new AutomationAuthorization(x.Access);
            x.Automation = new(x.Db, authorization, protection, TimeProvider.System);
            x.Db.Add(new AgentEntity { Id = x.Agent, Identity = "source-private-identity", Hostname = "Agent template", Status = "approved",
                ProtectedEnvironment = protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(x.Agent.ToString("N")).Protect("") });
            await x.Db.SaveChangesAsync(); x.Db.ChangeTracker.Clear();
            var support = new ParticipantSupport(x.Db, x.Access);
            var environment = new EnvironmentParticipant(x.Db, x.Settings, x.Agents, support);
            x.Participants = [new SourcesParticipant(x.Sources, support), environment, new AgentTemplatesParticipant(x.Db, x.Agents, environment, support),
                new PublicSettingsParticipant(x.Settings, support), new PoliciesParticipant(x.Policy, x.Automation, support),
                new AutomationParticipant(x.Db, x.Automation, authorization, support), new GroupsParticipant(x.Db, x.Access, support)];
            x.Portability = new(x.Db, x.Access, x.Drafts, x.Store, protection, TimeProvider.System, x.Participants);
            return x;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class NoDispatch : IAgentCommandDispatcher
    {
        public bool IsConnected(Guid agentId) => false;
        public Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("Portability configuration import/export must never dispatch Agent work.");
    }
}
