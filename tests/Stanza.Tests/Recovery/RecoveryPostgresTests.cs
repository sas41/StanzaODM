using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Stanza.Tests.Recovery;

// Opt-in integration test. Supply a disposable PostgreSQL connection whose account can
// create databases. Only uniquely named databases created here are ever dropped.
public sealed class RecoveryPostgresFactAttribute : FactAttribute
{
    public RecoveryPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STANZA_RECOVERY_POSTGRES")))
            Skip = "Set STANZA_RECOVERY_POSTGRES to an isolated PostgreSQL test server with CREATEDB permission.";
    }
}

public sealed class RecoveryPostgresTests
{
    [RecoveryPostgresFact]
    public async Task ActualPostgresRoundTripPreservesAllMappedRowsCredentialsAgentStateManagedFilesAndKeys()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("STANZA_RECOVERY_POSTGRES")) { Pooling = false };
        var prefix = "stanza_recovery_" + Guid.NewGuid().ToString("N");
        var databases = new List<string>();
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            foreach (var suffix in new[] { "_source", "_target" })
            {
                var database = prefix + suffix;
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
                await create.ExecuteNonQueryAsync(); databases.Add(database);
            }
            var sourceConnection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databases[0] }.ConnectionString;
            var targetConnection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databases[1] }.ConnectionString;
            await using var source = await RecoveryFixture.CreateAsync(postgresConnection: sourceConnection);
            var root = await source.Accounts.BootstrapAsync("postgres-root", RecoveryServiceTests.Password);
            await source.SeedAllTablesAsync();
            var secret = source.Protector.Protect("actual-postgres-recoverable-secret");
            source.Db.Settings.Add(new() { Key = "recovery-postgres-secret", Value = secret });
            await source.Db.SaveChangesAsync();
            var expected = await source.SnapshotAsync();
            var backup = await source.Service.BackupAsync(root.Id, RecoveryServiceTests.Passphrase);
            await using var target = await RecoveryFixture.CreateAsync(postgresConnection: targetConnection);
            await Assert.ThrowsAsync<Stanza.ControlPlane.Recovery.RecoveryOperationException>(() => target.Service.RestoreAsync(backup, "a-different-long-passphrase"));
            Assert.Empty(await target.Db.Accounts.ToListAsync());
            var result = await target.Service.RestoreAsync(backup, RecoveryServiceTests.Passphrase);
            Assert.True(result.RestartRequired);
            Assert.Equal(expected, await target.SnapshotAsync());
            Assert.NotNull(await target.Accounts.AuthenticateAsync("postgres-root", RecoveryServiceTests.Password));
            var restarted = DataProtectionProvider.Create(new DirectoryInfo(target.Keys), builder => builder.SetApplicationName("Stanza.ControlPlane")).CreateProtector("recovery-tests");
            Assert.Equal("actual-postgres-recoverable-secret", restarted.Unprotect((await target.Db.Settings.SingleAsync(x => x.Key == "recovery-postgres-secret")).Value));
        }
        finally
        {
            foreach (var database in databases)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
