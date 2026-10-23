using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Deployments;
using Stanza.Core.Security;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Editing;
using System.IO.Compression;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentDraftServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    private StanzaDbContext db = null!;
    private AccountService accounts = null!;
    private AccessService access = null!;
    private DeploymentDraftService drafts = null!;
    private AccountSummary root = null!;
    private AccountSummary admin = null!;
    private AccountSummary user = null!;
    private const string Password = "a-secure-password-123";

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(Options(connection));
        await db.Database.EnsureCreatedAsync();
        accounts = new(db, TimeProvider.System);
        access = new(db, TimeProvider.System);
        drafts = Service(db, protection);
        root = await accounts.BootstrapAsync("root", Password);
        admin = await accounts.CreateAsync(root.Id, "admin", Password, AccountRole.Admin);
        user = await accounts.CreateAsync(root.Id, "user", Password, AccountRole.User);
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Raw_text_and_binary_are_encrypted_and_survive_new_contexts_and_immutable_revisions()
    {
        const string compose = "\uFEFF# raw secret-compose\r\nservices:\n  app: {image: 'private/image'}\r\n";
        const string environment = "# secret-env\r\nA='multi\nline'\r\nEMPTY=\nESC=$$literal\n";
        const string dockerfile = "# secret-file\r\nFROM scratch\n";
        var created = await drafts.CreateAsync(root.Id, "  Exact name  ", null, compose, environment);
        Assert.Null(created.TargetAgentId);
        Assert.Equal(1, created.Revision);
        await drafts.SaveTextFileAsync(root.Id, created.Id, 1, "build/Dockerfile", dockerfile, true);
        byte[] binary = [0, 255, 13, 10, 128];
        await drafts.UploadAsync(admin.Id, created.Id, 2, "assets/data.bin", binary);
        binary[0] = 42;
        var saved = await drafts.ReadAsync(root.Id, created.Id);
        Assert.Equal(compose, saved.ComposeText);
        Assert.Equal(environment, saved.EnvironmentText);
        Assert.Equal(3, saved.ViewedRevision);
        Assert.Equal("  Exact name  ", saved.Draft.Name);
        Assert.Equal(dockerfile, (await drafts.ReadFileAsync(root.Id, created.Id, "build/Dockerfile")).GetText());
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, (await drafts.ReadFileAsync(root.Id, created.Id, "assets/data.bin")).GetBytes());
        var rows = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            var stored = Encoding.UTF8.GetString(row.ProtectedContent);
            Assert.DoesNotContain("secret-compose", stored);
            Assert.DoesNotContain("secret-env", stored);
            Assert.DoesNotContain("secret-file", stored);
        }
        await using var reopened = new StanzaDbContext(Options(connection));
        var afterRestart = Service(reopened, protection);
        Assert.Equal(environment, (await afterRestart.ReadAsync(admin.Id, created.Id)).EnvironmentText);
        Assert.Equal(Encoding.UTF8.GetBytes(compose), Encoding.UTF8.GetBytes((await afterRestart.ReadAsync(root.Id, created.Id)).ComposeText!));
        Assert.Empty((await afterRestart.ReadAsync(root.Id, created.Id, 1)).Files);
        Assert.DoesNotContain("secret-env", saved.ToString());
    }

    [Fact]
    public async Task Draft_edit_is_atomic_preserves_omitted_environment_and_does_not_fabricate_an_agent()
    {
        var created = await Create();
        var target = await ApprovedAgent();
        var edited = await drafts.SaveAsync(root.Id, created.Id, new("Edited", target, 1, "invalid yaml: [", null));
        Assert.Equal(target, edited.TargetAgentId);
        var current = await drafts.ReadAsync(root.Id, created.Id);
        Assert.Equal("invalid yaml: [", current.ComposeText);
        Assert.Equal("A=original\r\n", current.EnvironmentText);
        var old = await drafts.ReadAsync(root.Id, created.Id, 1);
        Assert.Equal("Draft", old.Draft.Name);
        Assert.Null(old.Draft.TargetAgentId);
        Assert.Equal("services: {}\r\n", old.ComposeText);
        Assert.Equal(1, old.ViewedRevision);
        await drafts.SaveAsync(root.Id, created.Id, new("Edited", null, 2, "", ""));
        var empty = await drafts.ReadAsync(root.Id, created.Id);
        Assert.Equal("", empty.ComposeText);
        Assert.Equal("", empty.EnvironmentText);
        Assert.Null(empty.Draft.TargetAgentId);
    }

    [Fact]
    public async Task File_crud_is_revisioned_and_cannot_overwrite_binary_or_reserved_files()
    {
        var created = await Create();
        await drafts.SaveTextFileAsync(root.Id, created.Id, 1, "file.txt", "first\r\n", true);
        await drafts.SaveTextFileAsync(root.Id, created.Id, 2, "file.txt", "second\n", false);
        await drafts.RenameFileAsync(root.Id, created.Id, 3, "file.txt", "directory/renamed.txt");
        Assert.Equal("first\r\n", (await drafts.ReadFileAsync(root.Id, created.Id, "file.txt", 2)).GetText());
        Assert.Equal("second\n", (await drafts.ReadFileAsync(root.Id, created.Id, "directory/renamed.txt")).GetText());
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadFileAsync(root.Id, created.Id, "file.txt"));
        await drafts.DeleteFileAsync(root.Id, created.Id, 4, "directory/renamed.txt");
        Assert.Empty((await drafts.ReadAsync(root.Id, created.Id)).Files);
        await drafts.UploadAsync(root.Id, created.Id, 5, "binary", [0, 255]);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 6, "binary", "new", false));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.UploadAsync(root.Id, created.Id, 6, "binary", [1]));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 6, "missing", "new", false));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.DeleteFileAsync(root.Id, created.Id, 6, "missing"));
        Assert.Equal(6, (await drafts.ReadAsync(root.Id, created.Id)).ViewedRevision);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/escape")]
    [InlineData("a\\file")]
    [InlineData("a/../../escape")]
    [InlineData("NUL.txt")]
    [InlineData("compose.yaml")]
    [InlineData("Compose.Yaml")]
    [InlineData(".env")]
    [InlineData(".ENV")]
    [InlineData("compose.yaml/child")]
    [InlineData(".env/child")]
    public async Task Unsafe_and_reserved_paths_do_not_create_a_revision(string path)
    {
        var created = await Create();
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 1, path, "text", true));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.UploadAsync(root.Id, created.Id, 1, path, [0]));
        Assert.Equal(1, (await drafts.ReadAsync(root.Id, created.Id)).ViewedRevision);
        Assert.Single(await db.Set<DeploymentDraftRevisionEntity>().ToListAsync());
    }

    [Fact]
    public async Task Case_and_parent_collisions_and_rename_collisions_are_rejected()
    {
        var created = await Create();
        await drafts.SaveTextFileAsync(root.Id, created.Id, 1, "Dir/file", "", true);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 2, "dir/other", "", true));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 2, "Dir/file/child", "", true));
        await drafts.SaveTextFileAsync(root.Id, created.Id, 2, "other", "", true);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.RenameFileAsync(root.Id, created.Id, 3, "other", "Dir/file"));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.RenameFileAsync(root.Id, created.Id, 3, "other", ".env"));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadFileAsync(root.Id, created.Id, ".env"));
        Assert.Equal(3, (await drafts.ReadAsync(root.Id, created.Id)).ViewedRevision);
    }

    [Fact]
    public async Task Ordinary_users_require_exact_catalog_grants_and_environment_reveal_is_separate()
    {
        var created = await Create();
        Assert.False(await drafts.CanCreateAsync(user.Id));
        Assert.Empty(await drafts.ListAsync(user.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ReadAsync(user.Id, created.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.CreateAsync(user.Id, "Denied", null, "", ""));
        await Grant("deployment.read", created.Id);
        var metadata = await drafts.ReadAsync(user.Id, created.Id);
        Assert.Null(metadata.ComposeText);
        Assert.Null(metadata.EnvironmentText);
        Assert.Single(await drafts.ListAsync(user.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ReadAsync(user.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.SaveAsync(user.Id, created.Id, new("Denied", null, 1, "", null)));
        await Grant("files.read", created.Id);
        var fileReader = await drafts.ReadAsync(user.Id, created.Id);
        Assert.NotNull(fileReader.ComposeText);
        Assert.Null(fileReader.EnvironmentText);
        await Grant("environment.read", created.Id);
        Assert.Null((await drafts.ReadAsync(user.Id, created.Id)).EnvironmentText);
        await Grant("environment.reveal", created.Id);
        Assert.Equal("A=original\r\n", (await drafts.ReadAsync(user.Id, created.Id)).EnvironmentText);
        await Grant("files.write", created.Id);
        await drafts.SaveAsync(user.Id, created.Id, new("Allowed", null, 1, "new", null));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.SaveAsync(user.Id, created.Id, new("Denied env", null, 2, null, "A=secret")));
        Assert.Equal(2, (await drafts.ReadAsync(root.Id, created.Id)).ViewedRevision);
        await Grant("environment.write", created.Id);
        await drafts.SaveAsync(user.Id, created.Id, new("Allowed env", null, 2, null, "A=new"));
        Assert.Equal("A=new", (await drafts.ReadAsync(user.Id, created.Id)).EnvironmentText);
    }

    [Fact]
    public async Task Group_grants_are_additive_revoked_immediately_and_create_grants_do_not_grant_read()
    {
        var created = await Create();
        var group = await access.CreateGroupAsync(root.Id, "Editors");
        await access.AddMemberAsync(root.Id, group.Id, user.Id);
        var grant = await access.AddGrantAsync(root.Id, null, group.Id, "deployment.read", "deployment", created.Id);
        Assert.Single(await drafts.ListAsync(user.Id));
        await access.RemoveGrantAsync(root.Id, grant.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ReadAsync(user.Id, created.Id));
        await access.AddGrantAsync(root.Id, user.Id, null, "deployment.create", "instance", null);
        var own = await drafts.CreateAsync(user.Id, "Created without implicit read", null, "", "");
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ReadAsync(user.Id, own.Id));
        Assert.Empty(await drafts.ListAsync(user.Id));
        await accounts.SetEnabledAsync(root.Id, user.Id, false);
        Assert.False(await drafts.CanCreateAsync(user.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ListAsync(user.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ListAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Every_file_mutation_rechecks_permission_and_cannot_use_another_deployments_grant()
    {
        var created = await Create();
        var another = await drafts.CreateAsync(root.Id, "Another", null, "", "");
        await Grant("deployment.read", created.Id);
        await Grant("files.read", created.Id);
        await Grant("files.write", another.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.SaveTextFileAsync(user.Id, created.Id, 1, "file", "", true));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.UploadAsync(user.Id, created.Id, 1, "file", []));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.RenameFileAsync(user.Id, created.Id, 1, "file", "new"));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.DeleteFileAsync(user.Id, created.Id, 1, "file"));
        var grant = await Grant("files.write", created.Id);
        await drafts.SaveTextFileAsync(user.Id, created.Id, 1, "file", "", true);
        await access.RemoveGrantAsync(root.Id, grant.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.DeleteFileAsync(user.Id, created.Id, 2, "file"));
    }

    [Fact]
    public async Task Stale_revisions_do_not_lose_updates_or_add_partial_history()
    {
        var created = await Create();
        await drafts.SaveTextFileAsync(root.Id, created.Id, 1, "first", "one", true);
        await Assert.ThrowsAsync<DeploymentDraftConflictException>(() => drafts.SaveAsync(root.Id, created.Id, new("Stale", null, 1, "stale", "stale")));
        await Assert.ThrowsAsync<DeploymentDraftConflictException>(() => drafts.UploadAsync(root.Id, created.Id, 1, "stale", []));
        var current = await drafts.ReadAsync(root.Id, created.Id);
        Assert.Equal("Draft", current.Draft.Name);
        Assert.Equal(2, current.History.Count);
        Assert.Equal("first", Assert.Single(current.Files).Path);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadAsync(root.Id, created.Id, 0));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadAsync(root.Id, created.Id, 3));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadAsync(root.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Failed_revision_insert_rolls_back_the_head_compare_and_swap()
    {
        var created = await Create();
        var duplicate = new DeploymentDraftRevisionEntity { DeploymentId = created.Id, Number = 2, ProtectedContent = [1] };
        db.Add(duplicate);
        await db.SaveChangesAsync();
        db.Entry(duplicate).State = EntityState.Detached;
        await Assert.ThrowsAsync<DbUpdateException>(() => drafts.SaveAsync(root.Id, created.Id, new("Must roll back", null, 1, "changed", null)));
        var head = await db.Set<DeploymentDraftEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(1, head.Revision);
        Assert.Equal("Draft", head.Name);
        Assert.Equal("services: {}\r\n", (await drafts.ReadAsync(root.Id, created.Id, 1)).ComposeText);
    }

    [Fact]
    public async Task Missing_keys_or_ciphertext_swapping_fail_closed_with_a_redacted_error()
    {
        var created = await Create();
        var wrongKeys = Service(db, new EphemeralDataProtectionProvider());
        var missing = await Assert.ThrowsAsync<DeploymentDraftException>(() => wrongKeys.ReadAsync(root.Id, created.Id));
        Assert.Contains("encryption keys", missing.Message);
        Assert.DoesNotContain("original", missing.Message);
        await drafts.SaveAsync(root.Id, created.Id, new("second", null, 1, "", null));
        var first = await db.Set<DeploymentDraftRevisionEntity>().AsNoTracking().SingleAsync(x => x.Number == 1);
        await db.Set<DeploymentDraftRevisionEntity>().Where(x => x.Number == 2)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProtectedContent, first.ProtectedContent));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ReadAsync(root.Id, created.Id));
        Assert.Equal("services: {}\r\n", (await drafts.ReadAsync(root.Id, created.Id, 1)).ComposeText);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    public async Task Invalid_draft_metadata_is_rejected(string name) =>
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, name, null, "", ""));

    [Fact]
    public async Task Text_upload_and_workspace_limits_are_enforced_before_any_revision_is_saved()
    {
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, new string('x', 101), null, "", ""));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, "Name", Guid.Empty, "", ""));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, "Name", null, "bad\0text", ""));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, "Name", null, "", "\uD800"));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, "Name", null, new string('雪', DeploymentDraftService.MaximumTextBytes / 3 + 1), ""));
        var created = await drafts.CreateAsync(root.Id, "Name", null, "", "");
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.UploadAsync(root.Id, created.Id, 1, "too-large", new byte[DeploymentDraftService.MaximumUploadBytes + 1]));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.SaveTextFileAsync(root.Id, created.Id, 1, "text", new string('x', DeploymentDraftService.MaximumTextBytes + 1), true));
        byte[] bytes = new byte[DeploymentDraftService.MaximumUploadBytes];
        for (var i = 0; i < 4; i++) await drafts.UploadAsync(root.Id, created.Id, i + 1, $"binary-{i}", bytes);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.UploadAsync(root.Id, created.Id, 5, "overflow", [1]));
        Assert.Equal(5, (await drafts.ReadAsync(root.Id, created.Id)).ViewedRevision);
    }

    [Fact]
    public async Task Supporting_file_count_limit_is_enforced()
    {
        var created = await drafts.CreateAsync(root.Id, "Count", null, "", "");
        for (var i = 0; i < DeploymentDraftService.MaximumSupportingFiles; i++)
            await drafts.UploadAsync(root.Id, created.Id, i + 1, $"file-{i}", []);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.UploadAsync(root.Id, created.Id, DeploymentDraftService.MaximumSupportingFiles + 1, "overflow", []));
        Assert.Equal(DeploymentDraftService.MaximumSupportingFiles, (await drafts.ReadAsync(root.Id, created.Id)).Files.Count);
    }

    [Fact]
    public async Task Simultaneous_writers_in_separate_database_connections_have_exactly_one_winner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stanza-draft-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = $"Data Source={path};Default Timeout=20;Pooling=False";
            Guid actor;
            Guid id;
            await using (var setupConnection = new SqliteConnection(connectionString))
            await using (var setup = new StanzaDbContext(Options(setupConnection)))
            {
                await setup.Database.EnsureCreatedAsync();
                actor = (await new AccountService(setup, TimeProvider.System).BootstrapAsync("root", Password)).Id;
                id = (await Service(setup, protection).CreateAsync(actor, "Original", null, "original", "")).Id;
            }
            using var start = new ManualResetEventSlim();
            Task<bool> Writer(string name) => Task.Run(async () =>
            {
                await using var writerConnection = new SqliteConnection(connectionString);
                await using var writer = new StanzaDbContext(Options(writerConnection));
                start.Wait();
                try
                {
                    await Service(writer, protection).SaveAsync(actor, id, new(name, null, 1, name, null));
                    return true;
                }
                catch (DeploymentDraftConflictException) { return false; }
            });
            var first = Writer("First");
            var second = Writer("Second");
            start.Set();
            var outcomes = await Task.WhenAll(first, second);
            Assert.Single(outcomes, won => won);
            await using var verifyConnection = new SqliteConnection(connectionString);
            await using var verify = new StanzaDbContext(Options(verifyConnection));
            var saved = await Service(verify, protection).ReadAsync(actor, id);
            Assert.Equal(2, saved.ViewedRevision);
            Assert.Equal(2, saved.History.Count);
            Assert.Equal(saved.Draft.Name, saved.ComposeText);
            Assert.Equal("original", (await Service(verify, protection).ReadAsync(actor, id, 1)).ComposeText);
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Fact]
    public async Task Persisted_instance_keys_allow_decryption_with_a_new_provider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stanza-draft-keys-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(path);
        try
        {
            var originalProvider = DataProtectionProvider.Create(directory);
            var created = await Service(db, originalProvider).CreateAsync(root.Id, "Persisted keys", null, "secret-compose\r\n", "secret-env\n");
            var newProvider = DataProtectionProvider.Create(directory);
            var loaded = await Service(db, newProvider).ReadAsync(root.Id, created.Id);
            Assert.Equal("secret-compose\r\n", loaded.ComposeText);
            Assert.Equal("secret-env\n", loaded.EnvironmentText);
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles()) file.Delete();
            directory.Delete();
        }
    }

    [Fact]
    public void PostgreSQL_schema_uses_encrypted_binary_content_and_a_concurrent_revision_head()
    {
        var options = new DbContextOptionsBuilder<StanzaDbContext>()
            .UseNpgsql("Host=localhost;Database=not-contacted;Username=not-contacted;Password=not-contacted").Options;
        using var postgres = new StanzaDbContext(options);
        var sql = postgres.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE \"DeploymentDrafts\"", sql);
        Assert.Contains("CREATE TABLE \"DeploymentDraftRevisions\"", sql);
        Assert.Contains("\"ProtectedContent\" bytea NOT NULL", sql);
        Assert.DoesNotContain("\"ComposeText\"", sql);
        Assert.DoesNotContain("\"EnvironmentText\"", sql);
        Assert.True(postgres.Model.FindEntityType(typeof(DeploymentDraftEntity))!.FindProperty(nameof(DeploymentDraftEntity.Revision))!.IsConcurrencyToken);
        Assert.Equal(2, postgres.Model.FindEntityType(typeof(DeploymentDraftRevisionEntity))!.FindPrimaryKey()!.Properties.Count);
    }

    private Task<DeploymentDraftSummary> Create() => drafts.CreateAsync(root.Id, "Draft", null, "services: {}\r\n", "A=original\r\n");

    private async Task<Guid> ApprovedAgent(string status = "approved", string hostname = "editor-agent")
    {
        var agent = new AgentEntity { Id = Guid.NewGuid(), Identity = Guid.NewGuid().ToString("N"), Hostname = hostname, Status = status };
        db.Add(agent); await db.SaveChangesAsync(); db.Entry(agent).State = EntityState.Detached;
        return agent.Id;
    }

    [Fact]
    public async Task Agent_selection_requires_registered_approved_and_readable_agents_including_on_save()
    {
        var approved = await ApprovedAgent();
        var pending = await ApprovedAgent("pending");
        var revoked = await ApprovedAgent("revoked");
        Assert.Equal(approved, Assert.Single(await drafts.ListTargetAgentsAsync(root.Id)).Id);
        foreach (var id in new[] { Guid.NewGuid(), pending, revoked })
            await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.CreateAsync(root.Id, "Denied", id, "", ""));
        await access.AddGrantAsync(root.Id, user.Id, null, "deployment.create", "instance", null);
        Assert.Empty(await drafts.ListTargetAgentsAsync(user.Id));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.CreateAsync(user.Id, "Denied", approved, "", ""));
        var grant = await access.AddGrantAsync(root.Id, user.Id, null, "agent.read", "agent", approved);
        Assert.Single(await drafts.ListTargetAgentsAsync(user.Id));
        var created = await drafts.CreateAsync(user.Id, "Allowed", approved, "", "");
        await Grant("deployment.read", created.Id); await Grant("files.write", created.Id);
        await access.RemoveGrantAsync(root.Id, grant.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.SaveAsync(user.Id, created.Id, new("Denied", approved, 1, null, null)));
        Assert.Equal(1, (await drafts.ReadAsync(root.Id, created.Id)).Draft.Revision);
        await drafts.SaveAsync(user.Id, created.Id, new("Unassigned", null, 1, null, null));
        Assert.Null((await drafts.ReadAsync(root.Id, created.Id)).Draft.TargetAgentId);
    }

    [Fact]
    public async Task Explicit_directory_rename_and_delete_include_descendants_and_preserve_old_revisions()
    {
        var created = await Create();
        await drafts.CreateDirectoryAsync(root.Id, created.Id, 1, "build");
        await drafts.CreateDirectoryAsync(root.Id, created.Id, 2, "build/empty");
        await drafts.UploadAsync(root.Id, created.Id, 3, "build/a.bin", [0, 255]);
        await drafts.RenameFileAsync(root.Id, created.Id, 4, "build", "context");
        Assert.Equal(new[] { "context", "context/empty", "context/a.bin" }, (await drafts.ReadAsync(root.Id, created.Id)).Files.Select(x => x.Path));
        Assert.Equal(new byte[] { 0, 255 }, (await drafts.ReadFileAsync(root.Id, created.Id, "context/a.bin")).GetBytes());
        Assert.Equal(ManagedEntryKind.Directory, (await drafts.ReadFileAsync(root.Id, created.Id, "build/empty", 4)).Kind);
        await drafts.DeleteFileAsync(root.Id, created.Id, 5, "context");
        Assert.Empty((await drafts.ReadAsync(root.Id, created.Id)).Files);
        Assert.Equal(3, (await drafts.ReadAsync(root.Id, created.Id, 5)).Files.Count);
    }

    [Fact]
    public async Task Archive_import_is_one_atomic_revision_and_preserves_binary_bytes()
    {
        var created = await Create();
        var zip = Archive(("empty/", []), ("build/data.bin", [0, 255, 13, 10]));
        await drafts.ImportArchiveAsync(root.Id, created.Id, 1, "assets", zip);
        var current = await drafts.ReadAsync(root.Id, created.Id);
        Assert.Equal(2, current.Draft.Revision); Assert.Equal(2, current.History.Count);
        Assert.Equal(2, current.Files.Count);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, (await drafts.ReadFileAsync(root.Id, created.Id, "assets/build/data.bin")).GetBytes());
        Assert.Empty((await drafts.ReadAsync(root.Id, created.Id, 1)).Files);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ImportArchiveAsync(root.Id, created.Id, 2, "assets", zip));
        Assert.Equal(2, (await drafts.ReadAsync(root.Id, created.Id)).Draft.Revision);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b")]
    [InlineData("C:/escape")]
    [InlineData("compose.yaml")]
    [InlineData(".env")]
    public async Task Archive_bad_paths_do_not_save_even_prior_safe_entries(string bad)
    {
        var created = await Create();
        await Assert.ThrowsAsync<DeploymentDraftException>(() => drafts.ImportArchiveAsync(root.Id, created.Id, 1, "", Archive(("safe", [1]), (bad, [2]))));
        Assert.Equal(1, (await drafts.ReadAsync(root.Id, created.Id)).Draft.Revision);
        Assert.Empty((await drafts.ReadAsync(root.Id, created.Id)).Files);
    }

    [Fact]
    public async Task Binary_replacement_and_new_mutations_cannot_bypass_write_permission()
    {
        var created = await Create();
        await drafts.UploadAsync(root.Id, created.Id, 1, "binary", [0, 255]);
        await Grant("deployment.read", created.Id);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.CreateDirectoryAsync(user.Id, created.Id, 2, "dir"));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ImportArchiveAsync(user.Id, created.Id, 2, "", Archive(("new", [1]))));
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => drafts.ReplaceBinaryAsync(user.Id, created.Id, 2, "binary", [1]));
        await Grant("files.write", created.Id);
        await drafts.ReplaceBinaryAsync(user.Id, created.Id, 2, "binary", [2, 3]);
        Assert.Equal(new byte[] { 2, 3 }, (await drafts.ReadFileAsync(root.Id, created.Id, "binary")).GetBytes());
        Assert.Equal(new byte[] { 0, 255 }, (await drafts.ReadFileAsync(root.Id, created.Id, "binary", 2)).GetBytes());
    }

    [Fact]
    public async Task Diffs_reveal_environment_only_with_reveal_grant_and_preserve_file_permissions()
    {
        var created = await Create();
        await drafts.SaveAsync(root.Id, created.Id, new("Updated", null, 1, "<script>\r\n", "SECRET=new\n"));
        await Grant("deployment.read", created.Id);
        Assert.Empty((await drafts.DiffAsync(user.Id, created.Id, 1)).Files);
        await Grant("files.read", created.Id);
        var filesOnly = await drafts.DiffAsync(user.Id, created.Id, 1);
        Assert.Equal("compose.yaml", Assert.Single(filesOnly.Files).Path);
        Assert.Contains(filesOnly.Files[0].Lines, x => x.Kind == "added" && x.Text == "<script>\r\n");
        await Grant("environment.read", created.Id);
        Assert.Single((await drafts.DiffAsync(user.Id, created.Id, 1)).Files);
        await Grant("environment.reveal", created.Id);
        Assert.Equal(2, (await drafts.DiffAsync(user.Id, created.Id, 1)).Files.Count);
    }

    private static byte[] Archive(params (string Path, byte[] Bytes)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var entry = archive.CreateEntry(file.Path).Open(); entry.Write(file.Bytes); }
        return output.ToArray();
    }

    [Fact]
    public async Task Editor_policy_provider_controls_text_binary_archive_entry_and_workspace_limits()
    {
        var policy = new EditorPolicy { Limits = new(8, 128, 256, 2) };
        var configured = new DeploymentDraftService(db, protection, accounts, access, TimeProvider.System, policy);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => configured.CreateAsync(root.Id, "Large", null, "123456789", ""));
        var created = await configured.CreateAsync(root.Id, "Small", null, "", "");
        await Assert.ThrowsAsync<DeploymentDraftException>(() => configured.UploadAsync(root.Id, created.Id, 1, "large", new byte[129]));
        await configured.CreateDirectoryAsync(root.Id, created.Id, 1, "dir");
        await configured.UploadAsync(root.Id, created.Id, 2, "dir/binary", [1]);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => configured.CreateDirectoryAsync(root.Id, created.Id, 3, "too-many"));
        policy.Limits = new(8, 128, 256, 3);
        await configured.CreateDirectoryAsync(root.Id, created.Id, 3, "now-allowed");
        Assert.Equal(4, (await configured.ReadAsync(root.Id, created.Id)).Draft.Revision);
    }

    [Fact]
    public async Task Reserved_git_destinations_block_managed_writes_without_exposing_checkout_files()
    {
        var created = await Create();
        var reservations = new ReservedSources();
        var configured = new DeploymentDraftService(db, protection, accounts, access, TimeProvider.System, reservations: reservations);
        await Assert.ThrowsAsync<DeploymentDraftException>(() => configured.SaveTextFileAsync(root.Id, created.Id, 1, "checkout/Dockerfile", "FROM scratch", true));
        await Assert.ThrowsAsync<DeploymentDraftException>(() => configured.ImportArchiveAsync(root.Id, created.Id, 1, "checkout", Archive(("file", [1]))));
        Assert.Equal(1, (await configured.ReadAsync(root.Id, created.Id)).Draft.Revision);
        await configured.SaveTextFileAsync(root.Id, created.Id, 1, "managed/Dockerfile", "FROM scratch", true);
        Assert.Equal("managed/Dockerfile", Assert.Single((await configured.ReadAsync(root.Id, created.Id)).Files).Path);
    }

    [Fact]
    public async Task Deployed_revision_status_comes_only_from_injected_operation_adapter()
    {
        var created = await Create();
        Assert.Null((await drafts.ReadAsync(root.Id, created.Id)).DeployedRevision);
        var configured = new DeploymentDraftService(db, protection, accounts, access, TimeProvider.System, revisionStatus: new DeployedStatus());
        Assert.Equal(1, (await configured.ReadAsync(root.Id, created.Id)).DeployedRevision);
        await configured.SaveAsync(root.Id, created.Id, new("Changed", null, 1, "new", null));
        var view = await configured.ReadAsync(root.Id, created.Id);
        Assert.Equal(2, view.Draft.Revision); Assert.Equal(1, view.DeployedRevision);
        await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => configured.ReadAsync(user.Id, created.Id));
    }

    private sealed class EditorPolicy : IDeploymentEditorLimitsProvider
    {
        public DeploymentEditorLimits Limits { get; set; } = DeploymentEditorLimits.Default;
        public Task<DeploymentEditorLimits> GetLimitsAsync(CancellationToken ct = default) => Task.FromResult(Limits);
    }

    private sealed class ReservedSources : IDeploymentWorkspaceReservations
    {
        public Task<IReadOnlyList<GitSourceWorkspace>> GetSourcesAsync(Guid deploymentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitSourceWorkspace>>([new(Guid.NewGuid(), "checkout")]);
    }

    private sealed class DeployedStatus : IDeploymentRevisionStatusProvider
    {
        public Task<long?> GetDeployedRevisionAsync(Guid deploymentId, CancellationToken ct = default) => Task.FromResult<long?>(1);
    }
    private Task<GrantSummary> Grant(string action, Guid id) => access.AddGrantAsync(root.Id, user.Id, null, action, "deployment", id);

    internal static DeploymentDraftService Service(StanzaDbContext context, IDataProtectionProvider provider) =>
        new(context, provider, new(context, TimeProvider.System), new(context, TimeProvider.System), TimeProvider.System);

    internal static DbContextOptions<StanzaDbContext> Options(SqliteConnection sqlite) =>
        new DbContextOptionsBuilder<StanzaDbContext>().UseSqlite(sqlite).Options;
}
