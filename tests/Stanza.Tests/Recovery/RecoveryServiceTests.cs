using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Recovery;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.Tests.Recovery;

public sealed class RecoveryServiceTests
{
    internal const string Passphrase = "offline-only-long-recovery-passphrase";
    internal const string Password = "the-account-password-123";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreScopeBackupsRestoreThenMigrateScopesAndDuplicateNamesUsingRestoredEncryptionKeys(bool beforeOutputStorage)
    {
        await using var source = await RecoveryFixture.CreateAsync();
        var owner = (await source.Accounts.BootstrapAsync("root", Password)).Id;
        var provider = DataProtectionProvider.Create(new DirectoryInfo(source.Keys), b => b.SetApplicationName("Stanza.ControlPlane"));
        var automation = new Stanza.ControlPlane.Automation.AutomationService(source.Db, new(new(source.Db, TimeProvider.System)), provider, TimeProvider.System);
        var deployment = Guid.NewGuid();
        var first = await automation.SaveActionAsync(owner, null, 0, "Deploy", true, "queue", new([new("deploy", deployment)], DeploymentId: deployment));
        var second = await automation.SaveActionAsync(owner, null, 0, "Deploy other", true, "queue", new([new("deploy", deployment)], DeploymentId: deployment));
        var archive = Decode(await source.Service.BackupAsync(owner, Passphrase));
        var actions = Table(archive, "AutomationActions");
        var oldNameIndex = actions["Columns"]!.AsArray().Select((x, i) => (x, i)).Single(x => x.x!["Name"]!.GetValue<string>() == "Name").i;
        foreach (var row in actions["Rows"]!.AsArray()) row![oldNameIndex]!["Value"] = "Deploy";
        for (var i = actions["Columns"]!.AsArray().Count - 1; i >= 0; i--)
            if (actions["Columns"]![i]!["Name"]!.GetValue<string>() is "ScopeDeploymentId" or "NormalizedName")
            {
                actions["Columns"]!.AsArray().RemoveAt(i);
                foreach (var row in actions["Rows"]!.AsArray()) row!.AsArray().RemoveAt(i);
            }
        if (beforeOutputStorage) archive["Tables"]!.AsArray().Remove(Table(archive, "OperationOutput"));
        var service = typeof(RecoveryService);
        var ordered = (List<ITable>)service.GetMethod("OrderedTables", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(source.Service, null)!;
        if (beforeOutputStorage) ordered = ordered.Where(x => x.Name != "OperationOutput").ToList();
        archive["SchemaFingerprint"] = (string)service.GetMethod("FingerprintSchema", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(source.Service, [ordered, true])!;
        await using var target = await RecoveryFixture.CreateAsync();
        await DatabaseUpgrader.UpgradeAsync(target.Db);
        await target.Service.RestoreAsync(Encode(archive), Passphrase);
        var restored = DataProtectionProvider.Create(new DirectoryInfo(target.Keys), b => b.SetApplicationName("Stanza.ControlPlane"));
        await DatabaseUpgrader.UpgradeAsync(target.Db, protection: restored);
        var saved = await target.Db.Set<Stanza.ControlPlane.Automation.ActionDefinition>().AsNoTracking().ToListAsync();
        Assert.Equal(new[] { first.Id, second.Id }.Order(), saved.Select(x => x.Id).Order());
        Assert.Equal(new[] { "Deploy", "Deploy (2)" }, saved.Select(x => x.Name).Order());
        Assert.All(saved, action => { Assert.Equal(deployment, action.ScopeDeploymentId); Assert.True(action.Enabled); });
        Assert.Equal("2", (await target.Db.Settings.AsNoTracking().SingleAsync(x => x.Key == "schema-version")).Value);
    }

    [Fact]
    public async Task PreOutputSchemaBackupRestoresOnAnUpgradedFreshInstanceWithoutLosingExistingData()
    {
        await using var source = await RecoveryFixture.CreateAsync();
        var root = await source.Accounts.BootstrapAsync("root", Password);
        source.Protector.Protect("generate-key");
        source.Db.Settings.Add(new() { Key = "saved-custom-value", Value = "preserved" }); await source.Db.SaveChangesAsync();
        var archive = Decode(await source.Service.BackupAsync(root.Id, Passphrase));
        var output = Table(archive, "OperationOutput"); archive["Tables"]!.AsArray().Remove(output);
        var service = typeof(RecoveryService);
        var ordered = (List<ITable>)service.GetMethod("OrderedTables", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(source.Service, null)!;
        var previous = ordered.Where(x => x.Name != "OperationOutput").ToList();
        archive["SchemaFingerprint"] = (string)service.GetMethod("Fingerprint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(source.Service, [previous])!;
        await using var target = await RecoveryFixture.CreateAsync();
        await DatabaseUpgrader.UpgradeAsync(target.Db);
        await target.Service.RestoreAsync(Encode(archive), Passphrase);
        Assert.NotNull(await target.Accounts.AuthenticateAsync("root", Password));
        Assert.Equal("preserved", (await target.Db.Settings.SingleAsync(x => x.Key == "saved-custom-value")).Value);
        Assert.Empty(await target.Db.Set<Stanza.ControlPlane.Operations.OperationOutputEntity>().ToListAsync());
        await DatabaseUpgrader.UpgradeAsync(target.Db);
        var restoredPolicy = JsonSerializer.Deserialize<Stanza.ControlPlane.Configuration.PolicyEditor>((await target.Db.Settings.SingleAsync(x => x.Key == "operational-policy")).Value)!;
        Assert.Equal(300, restoredPolicy.Policy.OperationTimeoutSeconds);
    }

    [Fact]
    public async Task SqliteRoundTripPreservesEveryMappedTablePasswordHashesManagedFilesAndRestartedSecrets()
    {
        await using var source = await RecoveryFixture.CreateAsync();
        var root = await source.Accounts.BootstrapAsync("root", Password);
        await source.SeedAllTablesAsync();
        await source.Db.Database.ExecuteSqlRawAsync("UPDATE \"AutomationQueueGuard\" SET \"Revision\" = 37");
        var secret = source.Protector.Protect("secret-that-must-survive-restart");
        source.Db.Settings.Add(new() { Key = "recovery-test-secret", Value = secret });
        await source.Db.SaveChangesAsync();
        var before = await source.SnapshotAsync();
        var hash = (await source.Db.Accounts.AsNoTracking().SingleAsync()).PasswordHash;
        Assert.DoesNotContain(hash, JsonSerializer.Serialize(await source.Db.Accounts.SingleAsync()));
        var backup = await source.Service.BackupAsync(root.Id, Passphrase);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(hash), backup);
        Assert.Equal(1, source.Audit.Backups);

        await using var target = await RecoveryFixture.CreateAsync();
        // A fresh instance can already have generated a DP key for CSRF/setup.
        var destinationSecret = target.Protector.Protect("destination-key-is-retained");
        var existingKeys = Directory.GetFiles(target.Keys);
        var result = await target.Service.RestoreAsync(backup, Passphrase);
        Assert.True(result.RestartRequired);
        var restoredStamp = (await target.Db.Accounts.AsNoTracking().SingleAsync()).SecurityStamp;
        Assert.NotEqual(root.SecurityStamp, restoredStamp);
        Assert.Equal(before.Replace(root.SecurityStamp, restoredStamp, StringComparison.Ordinal), await target.SnapshotAsync());
        Assert.All(existingKeys, path => Assert.True(File.Exists(path)));
        Assert.Equal(hash, (await target.Db.Accounts.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.NotNull(await target.Accounts.AuthenticateAsync("root", Password));
        var restarted = DataProtectionProvider.Create(new DirectoryInfo(target.Keys), builder => builder.SetApplicationName("Stanza.ControlPlane")).CreateProtector("recovery-tests");
        Assert.Equal("secret-that-must-survive-restart", restarted.Unprotect((await target.Db.Settings.SingleAsync(x => x.Key == "recovery-test-secret")).Value));
        Assert.Equal("destination-key-is-retained", restarted.Unprotect(destinationSecret));
        Assert.False(await target.Service.CanRestoreAsync());
        Assert.Equal(1, target.Audit.Restores);
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        await Assert.ThrowsAsync<SecurityOperationException>(() => target.Accounts.BootstrapAsync("another", Password));
    }

    [Theory]
    [InlineData(AccountRole.Admin)]
    [InlineData(AccountRole.User)]
    public async Task NonSystemAdminsCannotBackupEvenWithExportGrant(AccountRole role)
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var root = await fixture.Accounts.BootstrapAsync("root", Password);
        var actor = await fixture.Accounts.CreateAsync(root.Id, "actor", Password, role);
        fixture.Db.Grants.Add(new() { Id = Guid.NewGuid(), AccountId = actor.Id, Action = "configuration.export", ResourceType = "instance" });
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Service.CanBackupAsync(actor.Id));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => fixture.Service.BackupAsync(actor.Id, Passphrase));
        Assert.Equal(0, fixture.Audit.Backups);
    }

    [Fact]
    public async Task DisabledSystemAdminCannotBackup()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var root = await fixture.Accounts.BootstrapAsync("root", Password);
        var actor = await fixture.Accounts.CreateAsync(root.Id, "actor", Password, AccountRole.SystemAdmin);
        await fixture.Accounts.SetEnabledAsync(root.Id, actor.Id, false);
        await Assert.ThrowsAsync<RecoveryOperationException>(() => fixture.Service.BackupAsync(actor.Id, Passphrase));
    }

    [Theory]
    [InlineData("wrong-passphrase-that-is-long-enough")]
    [InlineData("short")]
    public async Task WrongPassphraseMakesNoDatabaseOrKeyChanges(string passphrase)
    {
        var backup = await CreateBackupAsync();
        await using var target = await RecoveryFixture.CreateAsync();
        var before = await target.SnapshotAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, passphrase));
        Assert.Equal(before, await target.SnapshotAsync());
        Assert.Empty(Directory.GetFiles(target.Keys));
        Assert.True(await target.Service.CanRestoreAsync());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("corrupt")]
    [InlineData("magic")]
    public async Task DamagedArchivesMakeNoWrites(string mode)
    {
        var backup = await CreateBackupAsync();
        if (mode == "truncated") backup = backup[..^1];
        else if (mode == "magic") backup[0] ^= 1;
        else backup[^1] ^= 1;
        await using var target = await RecoveryFixture.CreateAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
        Assert.Empty(Directory.GetFiles(target.Keys));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("format")]
    [InlineData("provider")]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("cell")]
    [InlineData("duplicate-key")]
    [InlineData("missing-admin")]
    [InlineData("missing-claim")]
    public async Task AuthenticatedButInvalidArchivesAreFullyValidatedBeforeClaimingSetup(string mode)
    {
        var archive = Decode(await CreateBackupAsync());
        switch (mode)
        {
            case "fingerprint": archive["SchemaFingerprint"] = "not-this-schema"; break;
            case "format": archive["FormatVersion"] = 999; break;
            case "provider": archive["Provider"] = "another-provider"; break;
            case "table": archive["Tables"]![0]!["Name"] = "\"; DROP TABLE SecurityAccounts; --"; break;
            case "column": archive["Tables"]![0]!["Columns"]![0]!["Name"] = "injected-column"; break;
            case "cell": Table(archive, "SecurityAccounts")["Rows"]![0]![0]!["Type"] = "unknown-type"; break;
            case "duplicate-key": archive["Keys"]!.AsArray().Add(archive["Keys"]![0]!.DeepClone()); break;
            case "missing-admin": Table(archive, "SecurityAccounts")["Rows"] = new JsonArray(); break;
            case "missing-claim": Table(archive, "SecurityGuard")["Rows"] = new JsonArray(); break;
        }
        await using var target = await RecoveryFixture.CreateAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(Encode(archive), Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
        Assert.Empty(Directory.GetFiles(target.Keys));
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("/tmp/outside.xml")]
    [InlineData("key-not-a-guid.xml")]
    [InlineData("key-00000000-0000-0000-0000-000000000000.xml/child")]
    public async Task KeyPathsCannotEscapeConfiguredKeyDirectory(string name)
    {
        var archive = Decode(await CreateBackupAsync());
        archive["Keys"]![0]!["Name"] = name;
        await using var target = await RecoveryFixture.CreateAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(Encode(archive), Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
    }

    [Fact]
    public async Task KeyXmlDtdAndMismatchedKeyIdentitiesAreRejectedBeforeWrites()
    {
        var archive = Decode(await CreateBackupAsync());
        archive["Keys"]![0]!["Content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<!DOCTYPE key [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><key id='00000000-0000-0000-0000-000000000000'>&secret;</key>"));
        await using var target = await RecoveryFixture.CreateAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(Encode(archive), Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
        archive["Keys"]![0]!["Content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<key id='00000000-0000-0000-0000-000000000000'/>"));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(Encode(archive), Passphrase));
    }

    [Fact]
    public async Task SymbolicKeyFilesAndDirectoryLinksAreRejectedAndUnrelatedFilesAreNotExported()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var source = await RecoveryFixture.CreateAsync();
        var root = await source.Accounts.BootstrapAsync("root", Password);
        source.Protector.Protect("generate-key");
        File.WriteAllText(Path.Combine(source.Keys, "unrelated.txt"), "must-not-export");
        var backup = await source.Service.BackupAsync(root.Id, Passphrase);
        Assert.DoesNotContain("must-not-export", Decode(backup).ToJsonString());
        var linkName = $"key-{Guid.NewGuid():D}.xml";
        File.CreateSymbolicLink(Path.Combine(source.Keys, linkName), Path.Combine(source.Keys, "unrelated.txt"));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => source.Service.BackupAsync(root.Id, Passphrase));
        await using var target = await RecoveryFixture.CreateAsync();
        var name = Decode(backup)["Keys"]![0]!["Name"]!.GetValue<string>();
        File.CreateSymbolicLink(Path.Combine(target.Keys, name), Path.Combine(source.Keys, "unrelated.txt"));
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
        File.Delete(Path.Combine(target.Keys, name));
        Directory.Delete(target.Keys);
        Directory.CreateSymbolicLink(target.Keys, source.Keys);
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Directory.Delete(target.Keys); // Remove only the test-owned link, never its target.
    }

    [Fact]
    public async Task ExistingDifferentKeyIsNeverOverwrittenAndFailedDatabaseRestoreRollsBackOnlyNewKeys()
    {
        var backup = await CreateBackupAsync();
        var archive = Decode(backup);
        var name = archive["Keys"]![0]!["Name"]!.GetValue<string>();
        await using var target = await RecoveryFixture.CreateAsync();
        var collision = Path.Combine(target.Keys, name);
        await File.WriteAllTextAsync(collision, "existing-different-key");
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.Equal("existing-different-key", await File.ReadAllTextAsync(collision));
        Assert.True(await target.Service.CanRestoreAsync());
        File.Delete(collision);
        target.Protector.Protect("keep-existing-destination-key");
        var existing = Directory.GetFiles(target.Keys).ToDictionary(x => x, File.ReadAllBytes);
        // Valid typed rows but a duplicate primary key forces a database failure after keys stage.
        var rows = Table(archive, "SecurityAccounts")["Rows"]!.AsArray();
        rows.Add(rows[0]!.DeepClone());
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(Encode(archive), Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
        Assert.Equal(existing.Keys.Order(), Directory.GetFiles(target.Keys).Order());
        foreach (var pair in existing) Assert.Equal(pair.Value, await File.ReadAllBytesAsync(pair.Key));
        Assert.Empty(await target.Db.Accounts.ToListAsync());
        Assert.Equal(0, target.Audit.Restores);
    }

    [Fact]
    public async Task NonemptyUnclaimedDatabaseIsNotOverwritten()
    {
        var backup = await CreateBackupAsync();
        await using var target = await RecoveryFixture.CreateAsync();
        target.Db.Settings.Add(new() { Key = "existing", Value = "retained" });
        await target.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.Equal("retained", (await target.Db.Settings.SingleAsync()).Value);
        Assert.True(await target.Service.CanRestoreAsync());
    }

    [Fact]
    public async Task ModifiedInfrastructureSeedRowsAreNotMistakenForFreshDefaults()
    {
        var backup = await CreateBackupAsync();
        await using var target = await RecoveryFixture.CreateAsync();
        await target.Db.Database.ExecuteSqlRawAsync("UPDATE \"AutomationQueueGuard\" SET \"Revision\" = 1");
        var before = await target.SnapshotAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.Equal(before, await target.SnapshotAsync());
        Assert.True(await target.Service.CanRestoreAsync());
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("keys")]
    [InlineData("key-bytes")]
    [InlineData("rows")]
    public async Task ArchiveAndEntryLimitsRejectUnboundedInput(string mode)
    {
        byte[] backup;
        if (mode == "oversize") backup = new byte[RecoveryService.MaximumArchiveBytes + 1];
        else
        {
            var archive = Decode(await CreateBackupAsync());
            if (mode == "keys")
                while (archive["Keys"]!.AsArray().Count <= 1024) archive["Keys"]!.AsArray().Add(archive["Keys"]![0]!.DeepClone());
            else if (mode == "key-bytes") archive["Keys"]![0]!["Content"] = Convert.ToBase64String(new byte[1024 * 1024 + 1]);
            else
            {
                var rows = Table(archive, "SecurityGuard")["Rows"]!.AsArray();
                var row = rows[0]!.DeepClone();
                while (rows.Count <= 100_000) rows.Add(row.DeepClone());
            }
            backup = Encode(archive);
        }
        await using var target = await RecoveryFixture.CreateAsync();
        await Assert.ThrowsAsync<RecoveryOperationException>(() => target.Service.RestoreAsync(backup, Passphrase));
        Assert.True(await target.Service.CanRestoreAsync());
    }

    [Fact]
    public async Task RestoreAndBootstrapShareOneAtomicFreshInstanceClaim()
    {
        var backup = await CreateBackupAsync();
        await using var target = await RecoveryFixture.CreateAsync(fileDatabase: true);
        await using var other = target.NewContext();
        var otherService = new AccountService(other, TimeProvider.System);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = Task.Run(async () => { await start.Task; return await CaptureAsync(() => target.Service.RestoreAsync(backup, Passphrase)); });
        var bootstrap = Task.Run(async () => { await start.Task; return await CaptureAsync(() => otherService.BootstrapAsync("new-root", Password)); });
        start.SetResult();
        var outcomes = await Task.WhenAll(restore, bootstrap);
        Assert.Single(outcomes, x => x is null);
        Assert.Single(await target.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.False(await target.Service.CanRestoreAsync());
    }

    [Fact]
    public async Task ConcurrentRestoresCannotBothClaimTheFreshInstance()
    {
        var backup = await CreateBackupAsync();
        await using var target = await RecoveryFixture.CreateAsync(fileDatabase: true);
        await using var other = target.NewContext();
        var competing = target.NewService(other);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await start.Task; return await CaptureAsync(() => target.Service.RestoreAsync(backup, Passphrase)); });
        var second = Task.Run(async () => { await start.Task; return await CaptureAsync(() => competing.RestoreAsync(backup, Passphrase)); });
        start.SetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, x => x is null);
        Assert.Single(await target.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.False(await target.Service.CanRestoreAsync());
    }

    private static async Task<Exception?> CaptureAsync<T>(Func<Task<T>> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    internal static async Task<byte[]> CreateBackupAsync()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var root = await fixture.Accounts.BootstrapAsync("root", Password);
        fixture.Protector.Protect("generate-persisted-key");
        return await fixture.Service.BackupAsync(root.Id, Passphrase);
    }

    internal static JsonNode Decode(byte[] encrypted)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Passphrase), encrypted.AsSpan(9, 32), 600_000, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[encrypted.Length - 69];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(41, 12), encrypted.AsSpan(69), encrypted.AsSpan(53, 16), plaintext, encrypted.AsSpan(0, 53));
        return JsonNode.Parse(plaintext)!;
    }

    internal static byte[] Encode(JsonNode archive)
    {
        var plaintext = Encoding.UTF8.GetBytes(archive.ToJsonString());
        var output = new byte[plaintext.Length + 69];
        "STANZA-R1"u8.CopyTo(output);
        RandomNumberGenerator.Fill(output.AsSpan(9, 44));
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Passphrase), output.AsSpan(9, 32), 600_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(41, 12), plaintext, output.AsSpan(69), output.AsSpan(53, 16), output.AsSpan(0, 53));
        return output;
    }

    internal static JsonNode Table(JsonNode archive, string name) => archive["Tables"]!.AsArray().Single(x => x!["Name"]!.GetValue<string>() == name)!;
}

internal sealed class RecoveryFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine("/tmp/opencode", "stanza-recovery-test-" + Guid.NewGuid().ToString("N"));
    public string Keys => Path.Combine(Root, "keys");
    public StanzaDbContext Db { get; private set; } = null!;
    public RecoveryService Service { get; private set; } = null!;
    public AccountService Accounts => new(Db, TimeProvider.System);
    public IDataProtector Protector => DataProtectionProvider.Create(new DirectoryInfo(Keys), builder => builder.SetApplicationName("Stanza.ControlPlane")).CreateProtector("recovery-tests");
    public TestRecoveryAudit Audit { get; } = new();
    private DbContextOptions<StanzaDbContext> options = null!;

    public static async Task<RecoveryFixture> CreateAsync(bool fileDatabase = false, string? postgresConnection = null)
    {
        var fixture = new RecoveryFixture();
        Directory.CreateDirectory(fixture.Keys);
        var builder = new DbContextOptionsBuilder<StanzaDbContext>();
        if (postgresConnection is not null) builder.UseNpgsql(postgresConnection);
        else builder.UseSqlite(fileDatabase ? $"Data Source={Path.Combine(fixture.Root, "instance.db")};Default Timeout=10;Pooling=False" : "Data Source=:memory:");
        fixture.options = builder.Options;
        fixture.Db = new(fixture.options);
        await fixture.Db.Database.OpenConnectionAsync();
        await fixture.Db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stanza:DataRoot"] = fixture.Root }).Build();
        fixture.Service = new(fixture.Db, configuration, fixture.Audit);
        return fixture;
    }

    public StanzaDbContext NewContext() => new(options);
    public RecoveryService NewService(StanzaDbContext context) => new(context,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stanza:DataRoot"] = Root }).Build(), Audit);

    public async Task SeedAllTablesAsync()
    {
        var remaining = Db.Model.GetRelationalModel().Tables.ToList();
        var done = new List<ITable>();
        while (remaining.Count > 0)
        {
            var table = remaining.First(x => x.ForeignKeyConstraints.All(fk => fk.PrincipalTable == x || done.Contains(fk.PrincipalTable)));
            done.Add(table); remaining.Remove(table);
            await using var count = Command($"SELECT COUNT(*) FROM {Quote(table.Name)}");
            if (Convert.ToInt64(await count.ExecuteScalarAsync()) > 0) continue;
            var columns = table.Columns.ToArray();
            var values = new Dictionary<string, object>();
            foreach (var column in columns)
            {
                var type = Nullable.GetUnderlyingType(column.PropertyMappings.First().Property.ClrType) ?? column.PropertyMappings.First().Property.ClrType;
                values[column.Name] = column.IsNullable ? DBNull.Value
                    : type == typeof(Guid) ? Guid.NewGuid()
                    : type == typeof(byte[]) ? new byte[] { 0, 1, 2, 255 }
                    : type == typeof(DateTimeOffset) ? DateTimeOffset.UtcNow
                    : type == typeof(DateTime) ? DateTime.UtcNow
                    : type == typeof(bool) ? true
                    : type == typeof(long) ? 1L
                    : type == typeof(int) || type.IsEnum ? 1
                    : type == typeof(double) ? 1.5d
                    : type == typeof(decimal) ? 1.5m : "retained-" + column.Name;
            }
            foreach (var fk in table.ForeignKeyConstraints)
            {
                // Grant's owner check requires exactly one owner, not both.
                if (table.Name == "SecurityGrants" && fk.Columns.Any(x => x.Name == "GroupId")) continue;
                for (var i = 0; i < fk.Columns.Count; i++)
                {
                    await using var principal = Command($"SELECT {Quote(fk.PrincipalColumns[i].Name)} FROM {Quote(fk.PrincipalTable.Name)} LIMIT 1");
                    values[fk.Columns[i].Name] = (await principal.ExecuteScalarAsync())!;
                }
            }
            await using var insert = Command($"INSERT INTO {Quote(table.Name)} ({string.Join(",", columns.Select(x => Quote(x.Name)))}) VALUES ({string.Join(",", columns.Select((_, i) => "@p" + i))})");
            for (var i = 0; i < columns.Length; i++)
            {
                var parameter = insert.CreateParameter(); parameter.ParameterName = "@p" + i; parameter.Value = values[columns[i].Name]; insert.Parameters.Add(parameter);
            }
            await insert.ExecuteNonQueryAsync();
        }
    }

    public async Task<string> SnapshotAsync()
    {
        var snapshot = new SortedDictionary<string, string[]>();
        foreach (var table in Db.Model.GetRelationalModel().Tables)
        {
            var rows = new List<string>();
            await using var command = Command($"SELECT {string.Join(",", table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => Quote(x.Name)))} FROM {Quote(table.Name)}");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values.Select(x => x is DBNull ? null : x)));
            }
            snapshot.Add(table.Name, rows.Order(StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(snapshot);
    }

    private string Quote(string identifier) => Db.GetService<ISqlGenerationHelper>().DelimitIdentifier(identifier);
    private DbCommand Command(string sql) { var command = Db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql; return command; }
    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Directory.Delete(Root, true); // Unique fixture-owned directory only.
    }
}

internal sealed class TestRecoveryAudit : IRecoveryAuditSink
{
    public int Backups { get; private set; }
    public int Restores { get; private set; }
    public Task RecordBackupAsync(Guid actorId, RecoveryResult result, CancellationToken token) { Backups++; return Task.CompletedTask; }
    public Task RecordRestoreAsync(RecoveryResult result, CancellationToken token) { Restores++; return Task.CompletedTask; }
}
