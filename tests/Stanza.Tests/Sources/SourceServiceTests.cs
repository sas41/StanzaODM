using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Core.Security;

namespace Stanza.Tests.Sources;

public sealed class SourceServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    private readonly Dispatcher dispatcher = new();
    private StanzaDbContext db = null!;
    private SourceService service = null!;
    private DeploymentDraftService drafts = null!;
    private AccessService access = null!;
    private AccountService accounts = null!;
    private Guid root, user, deployment, agent;
    private const string Url = "https://git.example.test/team/app.git";

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        accounts = new(db, TimeProvider.System); access = new(db, TimeProvider.System);
        root = (await accounts.BootstrapAsync("root", "a-secure-password-123")).Id;
        user = (await accounts.CreateAsync(root, "user", "a-secure-password-123", AccountRole.User)).Id;
        drafts = new(db, protection, accounts, access, TimeProvider.System);
        agent = Guid.NewGuid();
        db.Add(new AgentEntity { Id = agent, Identity = agent.ToString("N"), Hostname = "agent", Status = "approved" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        deployment = (await drafts.CreateAsync(root, "App", agent, "services: {}", "")).Id;
        service = new(db, access, protection, dispatcher);
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task Encrypted_resource_bound_secrets_are_never_in_metadata_and_reopen_correctly()
    {
        var saved = await GitCredential();
        var row = await db.Set<SourceCredentialEntity>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain("secret-password", row.ProtectedSecret);
        Assert.DoesNotContain("secret-user", row.ProtectedSecret);
        Assert.DoesNotContain("ProtectedSecret", JsonSerializer.Serialize(row));
        Assert.DoesNotContain("secret-password", JsonSerializer.Serialize(await service.ListCredentialsAsync(root)));
        Assert.DoesNotContain("secret-password", new CredentialInput("n", "git", Url, "u", "secret-password").ToString());
        await using var reopened = new StanzaDbContext(new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(connection).Options);
        var after = new SourceService(reopened, new(reopened, TimeProvider.System), protection, dispatcher);
        Assert.Equal(new CredentialSecret("secret-user", "secret-password"), await after.RevealAsync(root, saved.Id));
        var other = await service.SaveCredentialAsync(root, null, 0, new("other", "git", Url, "x", "y"));
        await db.Set<SourceCredentialEntity>().Where(x => x.Id == other.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedSecret, row.ProtectedSecret));
        await Assert.ThrowsAsync<SourceException>(() => service.RevealAsync(root, other.Id));
    }

    [Fact]
    public async Task Read_use_reveal_and_write_are_separate_and_revocation_blocks_existing_bindings()
    {
        var credential = await GitCredential();
        await Source(credential.Id);
        await Grant("deployment.read", "deployment", deployment);
        await Grant("credential.read", "credential", credential.Id);
        Assert.Single(await service.ListCredentialsAsync(user));
        await Assert.ThrowsAsync<SourceAccessException>(() => service.RevealAsync(user, credential.Id));
        await Assert.ThrowsAsync<SourceAccessException>(() => service.PrepareAsync(user, deployment));
        var grant = await access.AddGrantAsync(root, user, null, "credential.use", "credential", credential.Id);
        var snapshot = await service.PrepareAsync(user, deployment);
        Assert.Equal("secret-password", Assert.Single(snapshot.Git).Password);
        Assert.DoesNotContain("secret-password", snapshot.ToString());
        await Assert.ThrowsAsync<SourceAccessException>(() => service.RevealAsync(user, credential.Id));
        await access.RemoveGrantAsync(root, grant.Id);
        await Assert.ThrowsAsync<SourceAccessException>(() => service.PrepareAsync(user, deployment));
        await Assert.ThrowsAsync<SourceAccessException>(() => service.DeleteCredentialAsync(user, credential.Id, credential.Version));
    }

    [Fact]
    public async Task Scoped_reveal_does_not_grant_use_read_or_other_credential_access()
    {
        var one = await GitCredential(); var two = await GitCredential();
        await Grant("credential.reveal", "credential", one.Id);
        Assert.Equal("secret-password", (await service.RevealAsync(user, one.Id)).Password);
        Assert.Empty(await service.ListCredentialsAsync(user));
        await Assert.ThrowsAsync<SourceAccessException>(() => service.RevealAsync(user, two.Id));
        await Assert.ThrowsAsync<SourceAccessException>(() => service.TestCredentialAsync(user, one.Id, agent));
    }

    [Fact]
    public async Task Managing_sources_requires_all_deployment_permissions_and_credential_use()
    {
        var credential = await GitCredential();
        await Grant("deployment.read", "deployment", deployment);
        await Grant("sources.manage", "deployment", deployment);
        await Assert.ThrowsAsync<SourceAccessException>(() => service.SaveGitAsync(user, deployment, null, 0, new(Url, "main", "src", null)));
        await Grant("files.write", "deployment", deployment);
        await Assert.ThrowsAsync<SourceAccessException>(() => service.SaveGitAsync(user, deployment, null, 0, new(Url, "main", "src", credential.Id)));
        await Grant("credential.use", "credential", credential.Id);
        await service.SaveGitAsync(user, deployment, null, 0, new(Url, "main", "src", credential.Id));
        Assert.Single((await service.ReadAsync(user, deployment)).Git);
    }

    [Fact]
    public async Task Wrong_repository_or_credential_kind_is_rejected()
    {
        var git = await GitCredential(); var registry = await Registry();
        await Assert.ThrowsAsync<SourceException>(() => service.SaveGitAsync(root, deployment, null, 0, new("https://other.example.test/app.git", "main", "src", git.Id)));
        await Assert.ThrowsAsync<SourceException>(() => Source(registry.Id));
        await Assert.ThrowsAsync<SourceException>(() => service.SetRegistriesAsync(root, deployment, [git.Id], []));
    }

    [Fact]
    public async Task Credentials_cannot_be_deleted_or_retargeted_while_referenced()
    {
        var git = await GitCredential(); var source = await Source(git.Id);
        await Assert.ThrowsAsync<SourceException>(() => service.DeleteCredentialAsync(root, git.Id, git.Version));
        await Assert.ThrowsAsync<SourceException>(() => service.SaveCredentialAsync(root, git.Id, git.Version, new("n", "git", "https://other.test/a", "u", "p")));
        await service.DeleteGitAsync(root, deployment, source.Id, source.Version);
        await service.DeleteCredentialAsync(root, git.Id, git.Version);
        var registry = await Registry();
        await service.SetRegistriesAsync(root, deployment, [registry.Id], []);
        await Assert.ThrowsAsync<SourceException>(() => service.DeleteCredentialAsync(root, registry.Id, registry.Version));
        await service.SetRegistriesAsync(root, deployment, [], [registry.Id]);
        await service.DeleteCredentialAsync(root, registry.Id, registry.Version);
    }

    [Fact]
    public async Task Stale_writes_rollback_cleanly_and_next_valid_write_succeeds()
    {
        var credential = await GitCredential();
        await Assert.ThrowsAsync<SourceException>(() => service.SaveCredentialAsync(root, credential.Id, 99, new("n", "git", Url, "u", "p")));
        await service.SaveCredentialAsync(root, credential.Id, 1, new("n", "git", Url, "u", "p"));
        var source = await Source(credential.Id);
        await Assert.ThrowsAsync<SourceException>(() => service.DeleteGitAsync(root, deployment, source.Id, 99));
        await service.SaveGitAsync(root, deployment, source.Id, source.Version, new(Url, "refs/tags/v1", "newsrc", credential.Id));
        Assert.Equal("newsrc", Assert.Single((await service.ReadAsync(root, deployment)).Git).Destination);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.Entity is SourceCredentialEntity or DeploymentGitSourceEntity or DeploymentRegistryBindingEntity);
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData(".env")]
    [InlineData("assets")]
    [InlineData("assets/logo.png/child")]
    public async Task Checkout_paths_cannot_overlap_managed_files(string destination)
    {
        await drafts.UploadAsync(root, deployment, 1, "assets/logo.png", [1]);
        await Assert.ThrowsAsync<SourceException>(() => service.SaveGitAsync(root, deployment, null, 0, new(Url, "main", destination, null)));
    }

    [Fact]
    public async Task Multiple_sources_are_isolated_ordered_and_compose_is_untouched()
    {
        var one = await Source(null, "sources/one"); var two = await Source(null, "sources/two");
        await Assert.ThrowsAsync<SourceException>(() => Source(null, "SOURCES/ONE/nested"));
        await Assert.ThrowsAsync<SourceException>(() => service.ReorderAsync(root, deployment, [one.Id, one.Id], [1, 1]));
        await Assert.ThrowsAsync<SourceException>(() => service.ReorderAsync(root, deployment, [two.Id, one.Id], [99, 1]));
        await service.ReorderAsync(root, deployment, [two.Id, one.Id], [1, 1]);
        Assert.Equal(new[] { two.Id, one.Id }, (await service.ReadAsync(root, deployment)).Git.Select(x => x.Id));
        Assert.Equal("services: {}", (await drafts.ReadAsync(root, deployment)).ComposeText);
        var snapshot = await service.PrepareAsync(root, deployment);
        Assert.Equal(2, snapshot.Git.Count); Assert.Null(snapshot.Git[0].Password);
    }

    [Fact]
    public async Task Execution_revalidates_collisions_added_after_source_configuration()
    {
        await Source(null, "src");
        await drafts.SaveTextFileAsync(root, deployment, 1, "src/Dockerfile", "FROM scratch", true);
        await Assert.ThrowsAsync<SourceException>(() => service.PrepareAsync(root, deployment));
    }

    [Fact]
    public async Task Registry_bindings_are_distinct_authorized_optimistic_and_snapshot_credentials()
    {
        var registry = await Registry(); var same = await Registry();
        await Assert.ThrowsAsync<SourceException>(() => service.SetRegistriesAsync(root, deployment, [registry.Id, same.Id], []));
        await service.SetRegistriesAsync(root, deployment, [registry.Id], []);
        await Assert.ThrowsAsync<SourceException>(() => service.SetRegistriesAsync(root, deployment, [], []));
        Assert.Single((await service.ReadAsync(root, deployment)).RegistryCredentialIds);
        var snapshot = await service.PrepareAsync(root, deployment);
        Assert.Equal(new AgentRegistryCredential("registry.example.test", "registry-user", "registry-password"), Assert.Single(snapshot.Registries));
        await Grant("deployment.read", "deployment", deployment);
        await Assert.ThrowsAsync<SourceAccessException>(() => service.PrepareAsync(user, deployment));
        await Grant("credential.use", "credential", registry.Id);
        Assert.Single((await service.PrepareAsync(user, deployment)).Registries);
    }

    [Fact]
    public async Task Branch_browser_is_bounded_searchable_and_preserves_deleted_selection()
    {
        var git = await GitCredential(); var source = await Source(git.Id);
        dispatcher.Response = JsonSerializer.Serialize(Enumerable.Range(0, 125).Select(x => $"refs/heads/feature-{x:D3}").Append("refs/heads/main"));
        var first = await service.BranchesAsync(root, deployment, source.Id);
        Assert.False(first.SelectedMissing); Assert.Equal(50, first.References.Count); Assert.True(first.HasMore);
        var searched = await service.BranchesAsync(root, deployment, source.Id, "feature-12");
        Assert.Equal(5, searched.References.Count); Assert.False(searched.HasMore);
        var last = await service.BranchesAsync(root, deployment, source.Id, page: 2);
        Assert.Equal(26, last.References.Count); Assert.False(last.HasMore);
        dispatcher.Response = "[\"refs/heads/other\"]";
        var deleted = await service.BranchesAsync(root, deployment, source.Id);
        Assert.True(deleted.SelectedMissing); Assert.Equal("main", deleted.Selected);
        Assert.Equal("git-branches", dispatcher.Last!.Operation); Assert.Empty(dispatcher.Last.Files);
        Assert.Equal("secret-password", Assert.Single(dispatcher.Last.Sources!).Password);
        Assert.Equal(agent, dispatcher.Agent);
    }

    [Fact]
    public async Task Branch_checks_require_agent_read_approved_target_and_current_credential_use()
    {
        var git = await GitCredential(); var source = await Source(git.Id);
        await Grant("deployment.read", "deployment", deployment);
        await Grant("credential.use", "credential", git.Id);
        await Assert.ThrowsAsync<SourceAccessException>(() => service.BranchesAsync(user, deployment, source.Id));
        await Grant("agent.read", "agent", agent);
        await service.BranchesAsync(user, deployment, source.Id);
        await db.Set<AgentEntity>().Where(x => x.Id == agent).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "pending"));
        await Assert.ThrowsAsync<SourceException>(() => service.BranchesAsync(user, deployment, source.Id));
    }

    [Fact]
    public async Task Connection_tests_use_only_explicit_approved_agent_and_sanitize_failures()
    {
        var registry = await Registry();
        await service.TestCredentialAsync(root, registry.Id, agent);
        Assert.Equal("registry-check", dispatcher.Last!.Operation); Assert.Empty(dispatcher.Last.Files);
        Assert.Single(dispatcher.Last.Registries!);
        await Assert.ThrowsAsync<SourceException>(() => service.TestCredentialAsync(root, registry.Id, Guid.NewGuid()));
        dispatcher.Succeeded = false; dispatcher.Response = "secret-password https://user:password@host";
        var error = await Assert.ThrowsAsync<SourceException>(() => service.TestCredentialAsync(root, registry.Id, agent));
        Assert.DoesNotContain("secret-password", error.Message); Assert.DoesNotContain("https://user", error.Message);
        dispatcher.Offline = true;
        Assert.Contains("offline", (await Assert.ThrowsAsync<SourceException>(() => service.TestCredentialAsync(root, registry.Id, agent))).Message);
    }

    [Fact]
    public async Task Snapshots_and_mutations_reuse_ambient_transactions_without_committing_caller_work()
    {
        var credential = await GitCredential();
        await Source(credential.Id);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.Single((await service.PrepareAsync(root, deployment)).Git);
            var extra = await Source(null, "extra");
            await Assert.ThrowsAsync<SourceException>(() => service.DeleteGitAsync(root, deployment, extra.Id, 99));
            Assert.Equal(2, (await service.ReadAsync(root, deployment)).Git.Count);
            await transaction.RollbackAsync();
        }
        Assert.Single((await service.ReadAsync(root, deployment)).Git);
    }

    [Fact]
    public async Task Invalid_agent_output_is_not_reflected_as_html_or_secret_errors()
    {
        var source = await Source(null);
        dispatcher.Response = "<script>secret-password</script>";
        var error = await Assert.ThrowsAsync<SourceException>(() => service.BranchesAsync(root, deployment, source.Id));
        Assert.DoesNotContain("secret-password", error.Message);
        dispatcher.Response = JsonSerializer.Serialize(new[] { "-bad-ref" });
        await Assert.ThrowsAsync<SourceException>(() => service.BranchesAsync(root, deployment, source.Id));
        dispatcher.Response = JsonSerializer.Serialize(Enumerable.Range(0, 10001).Select(x => "branch-" + x));
        await Assert.ThrowsAsync<SourceException>(() => service.BranchesAsync(root, deployment, source.Id));
    }

    private Task<GrantSummary> Grant(string action, string type, Guid id) => access.AddGrantAsync(root, user, null, action, type, id);
    private Task<CredentialSummary> GitCredential() => service.SaveCredentialAsync(root, null, 0, new("Git", "git", Url, "secret-user", "secret-password"));
    private Task<CredentialSummary> Registry() => service.SaveCredentialAsync(root, null, 0, new("Registry", "registry", "registry.example.test", "registry-user", "registry-password"));
    private Task<GitSourceSummary> Source(Guid? credential, string destination = "src") => service.SaveGitAsync(root, deployment, null, 0, new(Url, "main", destination, credential));

    private sealed class Dispatcher : IAgentCommandDispatcher
    {
        public AgentWorkRequest? Last { get; private set; }
        public Guid Agent { get; private set; }
        public string Response { get; set; } = "[\"refs/heads/main\"]";
        public bool Succeeded { get; set; } = true;
        public bool Offline { get; set; }
        public bool IsConnected(Guid id) => !Offline;
        public Task<AgentWorkResponse> DispatchAsync(Guid id, AgentWorkRequest request, CancellationToken token = default)
        {
            if (Offline) throw new AgentUnavailableException();
            Agent = id; Last = request;
            return Task.FromResult(new AgentWorkResponse(request.Id, Succeeded, Succeeded ? "ok" : "failed", Response));
        }
    }
}
