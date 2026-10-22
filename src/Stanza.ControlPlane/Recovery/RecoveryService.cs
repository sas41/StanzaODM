using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Security;
using Stanza.ControlPlane.Configuration;

namespace Stanza.ControlPlane.Recovery;

public sealed record RecoveryResult(int TableCount, int RowCount, bool RestartRequired = true);

// No archive, passphrase, keys, hashes, names, or row values enter the audit contract.
public interface IRecoveryAuditSink
{
    Task RecordBackupAsync(Guid actorId, RecoveryResult result, CancellationToken token);
    Task RecordRestoreAsync(RecoveryResult result, CancellationToken token);
}

internal sealed class NullRecoveryAuditSink : IRecoveryAuditSink
{
    public Task RecordBackupAsync(Guid actorId, RecoveryResult result, CancellationToken token) => Task.CompletedTask;
    public Task RecordRestoreAsync(RecoveryResult result, CancellationToken token) => Task.CompletedTask;
}

public static class RecoveryRegistration
{
    public static IServiceCollection AddStanzaRecovery(this IServiceCollection services)
    {
        services.TryAddScoped<IRecoveryAuditSink, NullRecoveryAuditSink>();
        services.TryAddScoped<RecoveryService>();
        return services;
    }
}

public sealed class RecoveryService(StanzaDbContext db, IConfiguration configuration, IRecoveryAuditSink audit, InstanceMaintenance? maintenance = null)
{
    public const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private const int MaximumTables = 256, MaximumColumns = 512, MaximumRows = 100_000, MaximumCells = 1_000_000;
    private const long MaximumValueBytes = 80L * 1024 * 1024;
    private string KeyDirectory => Path.Combine(Path.GetFullPath(configuration["Stanza:DataRoot"] ?? "data/control-plane"), "keys");

    public async Task<bool> CanRestoreAsync(CancellationToken token = default) =>
        !await db.Accounts.AnyAsync(token) && !await db.Set<SecurityGuard>().AnyAsync(x => x.Id == 1, token);

    public async Task<bool> CanBackupAsync(Guid actorId, CancellationToken token = default) =>
        await db.Accounts.AnyAsync(x => x.Id == actorId && x.Enabled && x.Role == AccountRole.SystemAdmin, token);

    public async Task<byte[]> BackupAsync(Guid actorId, string passphrase, CancellationToken token = default)
    {
        RecoveryEncryption.ValidatePassphrase(passphrase);
        if (!await CanBackupAsync(actorId, token)) throw new RecoveryOperationException("Only an enabled System Admin can create an instance backup.");
        var tables = OrderedTables();
        var archive = new RecoveryArchive
        {
            Provider = db.Database.ProviderName!, SchemaFingerprint = Fingerprint(tables), CreatedAt = DateTimeOffset.UtcNow
        };
        try
        {
            var isolation = IsPostgres ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable;
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, token);
            if (IsPostgres) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            // Recheck authority in the same consistent snapshot as the backup.
            if (!await CanBackupAsync(actorId, token)) throw new RecoveryOperationException("Only an enabled System Admin can create an instance backup.");
            long bytes = 0;
            int rows = 0, cells = 0;
            foreach (var table in tables)
            {
                var saved = Describe(table);
                await using var command = Command($"SELECT {string.Join(", ", saved.Columns.Select(x => Quote(x.Name)))} FROM {Quote(table)}");
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (++rows > MaximumRows || (cells += saved.Columns.Count) > MaximumCells) Limit();
                    var row = new RecoveryCell[saved.Columns.Count];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = RecoveryCell.From(reader.GetValue(i));
                        bytes += row[i].Value is { } value ? Encoding.UTF8.GetByteCount(value) : 0;
                        if (bytes > MaximumValueBytes) Limit();
                    }
                    saved.Rows.Add(row);
                }
                archive.Tables.Add(saved);
            }
            // Keys are additive in the running application. Read them after the DB snapshot so
            // every key which could have encrypted a captured value is included.
            archive.Keys = RecoveryKeys.Read(KeyDirectory);
            await transaction.CommitAsync(token);
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException)
        {
            throw new RecoveryOperationException("The instance snapshot could not be read. Check database and persisted-key storage availability.");
        }
        Validate(archive, tables);
        var encrypted = RecoveryEncryption.Encrypt(archive, passphrase);
        await audit.RecordBackupAsync(actorId, Result(archive) with { RestartRequired = false }, token);
        return encrypted;
    }

    public async Task<RecoveryResult> RestoreAsync(byte[] encrypted, string passphrase, CancellationToken token = default)
    {
        // No database writes (including bootstrap claims) occur until decryption, schema,
        // structural, typed-value, and key-file validation have all succeeded.
        if (!await CanRestoreAsync(token)) throw new RecoveryOperationException("Restore is only available on an unclaimed fresh instance before initial setup.");
        var archive = RecoveryEncryption.Decrypt(encrypted, passphrase);
        var tables = OrderedTables();
        Validate(archive, tables);
        using var maintenanceLease = maintenance?.BeginRestore();
        var createdKeys = new List<string>();
        var committed = false;
        var commitAttempted = false;
        try
        {
            RecoveryKeys.CheckCollisions(KeyDirectory, archive.Keys);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var claimed = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"SecurityGuard\" (\"Id\", \"Revision\") VALUES (1, 0) ON CONFLICT (\"Id\") DO NOTHING", token);
            if (claimed != 1 || await db.Accounts.AnyAsync(token))
                throw new RecoveryOperationException("Initial setup or another restore has already claimed this instance.");
            foreach (var table in tables)
            {
                if (table.Name == "SecurityGuard" && table.Schema is null) continue;
                await CheckFreshTableAsync(table, token);
            }
            RecoveryKeys.Stage(KeyDirectory, archive.Keys, createdKeys);
            // The fresh-instance claim and every restored row share one transaction. Deleting
            // and restoring the guard inside it never opens a concurrent-bootstrap window.
            // EnsureCreated can supply infrastructure seed rows on a genuinely fresh instance.
            // Only exact model-declared defaults passed the freshness check above; remove them
            // in reverse FK order so the archive can restore their captured state as well.
            foreach (var table in tables.AsEnumerable().Reverse())
            {
                await using var clear = Command($"DELETE FROM {Quote(table)}");
                await clear.ExecuteNonQueryAsync(token);
            }
            foreach (var table in tables)
            {
                var saved = archive.Tables.Single(x => x.Name == table.Name && x.Schema == table.Schema);
                var sql = $"INSERT INTO {Quote(table)} ({string.Join(", ", saved.Columns.Select(x => Quote(x.Name)))}) VALUES ({string.Join(", ", saved.Columns.Select((_, i) => "@v" + i))})";
                await using var command = Command(sql);
                for (var i = 0; i < saved.Columns.Count; i++)
                {
                    var parameter = command.CreateParameter(); parameter.ParameterName = "@v" + i; command.Parameters.Add(parameter);
                }
                foreach (var row in saved.Rows)
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        var value = row[i].Decode();
                        var parameter = command.Parameters[i];
                        parameter.ResetDbType();
                        parameter.Value = value;
                    }
                    await command.ExecuteNonQueryAsync(token);
                }
            }
            commitAttempted = true;
            // Restoring a backup must not resurrect previously issued browser sessions.
            var restoredStamp = Guid.NewGuid().ToString("N");
            await db.Accounts.ExecuteUpdateAsync(update => update.SetProperty(x => x.SecurityStamp, restoredStamp), token);
            await transaction.CommitAsync(token);
            committed = true;
            db.ChangeTracker.Clear();
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException)
        {
            throw new RecoveryOperationException(commitAttempted
                ? "The database commit could not be confirmed. Newly added encryption keys were retained for safety. Stop the Control Plane and verify the database state before attempting recovery again."
                : "Restore failed and database changes were rolled back. Check database constraints and key-storage availability; do not use this instance until recovery succeeds.");
        }
        finally
        {
            if (commitAttempted) maintenance?.RequireRestart();
            if (!committed && !commitAttempted)
                foreach (var path in createdKeys) File.Delete(path); // Only files created by this attempt; never existing keys.
        }
        var result = Result(archive);
        await audit.RecordRestoreAsync(result, token);
        return result;
    }

    private bool IsPostgres => db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    private async Task CheckFreshTableAsync(ITable table, CancellationToken token)
    {
        await using var count = Command($"SELECT COUNT(*) FROM {Quote(table)}");
        var actual = Convert.ToInt64(await count.ExecuteScalarAsync(token));
        if (actual == 0) return;
        var store = StoreObjectIdentifier.Table(table.Name, table.Schema);
        var defaults = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(x => x.GetTableName() == table.Name && x.GetSchema() == table.Schema)
            .SelectMany(entity => entity.GetSeedData(providerValues: true).Select(seed =>
                entity.GetProperties().Where(p => p.GetColumnName(store) is not null)
                    .ToDictionary(p => p.GetColumnName(store)!, p => seed.TryGetValue(p.Name, out var value) ? value : null, StringComparer.Ordinal)))
            .ToList();
        if (actual != defaults.Count) NotFresh();
        foreach (var row in defaults)
        {
            if (row.Count != table.Columns.Count()) NotFresh();
            await using var match = Command("");
            var predicates = new List<string>();
            foreach (var column in table.Columns)
            {
                if (!row.TryGetValue(column.Name, out var value)) NotFresh();
                if (value is null or DBNull) predicates.Add($"{Quote(column.Name)} IS NULL");
                else
                {
                    var parameter = match.CreateParameter(); parameter.ParameterName = "@s" + match.Parameters.Count; parameter.Value = value;
                    match.Parameters.Add(parameter); predicates.Add($"{Quote(column.Name)} = {parameter.ParameterName}");
                }
            }
            match.CommandText = $"SELECT COUNT(*) FROM {Quote(table)} WHERE {string.Join(" AND ", predicates)}";
            if (Convert.ToInt64(await match.ExecuteScalarAsync(token)) != 1) NotFresh();
        }
    }

    private static void NotFresh() => throw new RecoveryOperationException("Restore requires a fresh instance database containing only initial model defaults; existing application data will never be overwritten.");

    private List<ITable> OrderedTables()
    {
        if (!IsPostgres && db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new RecoveryOperationException("This database provider does not support instance recovery.");
        var remaining = db.Model.GetRelationalModel().Tables.OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        if (remaining.Count is < 1 or > MaximumTables || remaining.Any(x => x.Columns.Count() is < 1 or > MaximumColumns)) Limit();
        var ordered = new List<ITable>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(x => x.ForeignKeyConstraints.All(fk => ordered.Contains(fk.PrincipalTable)));
            if (next is null) throw new RecoveryOperationException("This database schema contains cyclic foreign keys and cannot be safely restored.");
            // Generated columns require a dedicated adapter rather than pretending to preserve them.
            if (next.Columns.Any(x => x.ComputedColumnSql is not null))
                throw new RecoveryOperationException("This database schema contains unsupported generated recovery columns.");
            ordered.Add(next); remaining.Remove(next);
        }
        return ordered;
    }

    private void Validate(RecoveryArchive archive, List<ITable> tables)
    {
        if (archive.FormatVersion != 1 || archive.Application != "Stanza.ControlPlane" || archive.Provider != db.Database.ProviderName
            || archive.SchemaFingerprint != Fingerprint(tables))
            throw new RecoveryOperationException("The backup format or application database schema is incompatible. Restore using the same application schema and PostgreSQL provider; migrations and legacy imports are not supported.");
        if (archive.Tables is null || archive.Tables.Count != tables.Count || archive.Keys is null || archive.Keys.Count is < 1 or > RecoveryKeys.MaximumKeys)
            throw new RecoveryOperationException("The recovery archive has invalid table or encryption-key counts.");
        int rows = 0, cells = 0;
        long bytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in archive.Keys)
        {
            RecoveryKeys.Validate(key);
            if (!names.Add(key.Name)) throw new RecoveryOperationException("The archive contains duplicate encryption-key filenames.");
            bytes += key.Content.Length;
        }
        try
        {
            foreach (var table in tables)
            {
                var matches = archive.Tables.Where(x => x is not null && x.Name == table.Name && x.Schema == table.Schema).ToArray();
                if (matches.Length != 1) throw new RecoveryOperationException("The recovery archive has missing or duplicate tables.");
                var saved = matches[0];
                var trusted = Describe(table);
                if (saved.Columns is null || saved.Rows is null || saved.Columns.Count != trusted.Columns.Count
                    || !saved.Columns.Zip(trusted.Columns).All(x => x.First is not null && x.First.Name == x.Second.Name && x.First.StoreType == x.Second.StoreType && x.First.Nullable == x.Second.Nullable))
                    throw new RecoveryOperationException("The recovery archive column definitions do not match the application schema.");
                foreach (var row in saved.Rows)
                {
                    if (row is null || row.Length != saved.Columns.Count) throw new RecoveryOperationException("The recovery archive has an invalid row shape.");
                    if (++rows > MaximumRows || (cells += row.Length) > MaximumCells) Limit();
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (row[i] is null) throw new RecoveryOperationException("The recovery archive has an invalid cell.");
                        var decoded = row[i].Decode();
                        if (decoded is DBNull && !saved.Columns[i].Nullable) throw new RecoveryOperationException("The recovery archive has a null required value.");
                        bytes += row[i].Value is { } value ? Encoding.UTF8.GetByteCount(value) : 0;
                        if (bytes > MaximumValueBytes) Limit();
                    }
                }
            }
            // A valid instance backup must close setup after restoration and preserve a usable
            // System Admin. Never permit an archive to reopen bootstrap over restored state.
            var guard = archive.Tables.Single(x => x.Name == "SecurityGuard");
            var guardId = guard.Columns.FindIndex(x => x.Name == "Id");
            var accounts = archive.Tables.Single(x => x.Name == "SecurityAccounts");
            var enabled = accounts.Columns.FindIndex(x => x.Name == "Enabled");
            var role = accounts.Columns.FindIndex(x => x.Name == "Role");
            if (!guard.Rows.Any(x => Convert.ToInt64(x[guardId].Decode()) == 1)
                || !accounts.Rows.Any(x => Convert.ToBoolean(x[enabled].Decode()) && Convert.ToInt32(x[role].Decode()) == (int)AccountRole.SystemAdmin))
                throw new RecoveryOperationException("The recovery archive does not contain a claimed instance with an enabled System Admin.");
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            throw new RecoveryOperationException("The recovery archive contains malformed typed values.");
        }
    }

    private static RecoveryResult Result(RecoveryArchive archive) => new(archive.Tables.Count, archive.Tables.Sum(x => x.Rows.Count));

    private static RecoveryTable Describe(ITable table) => new()
    {
        Name = table.Name, Schema = table.Schema,
        Columns = table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new RecoveryColumn { Name = x.Name, StoreType = x.StoreType, Nullable = x.IsNullable }).ToList()
    };

    private string Fingerprint(List<ITable> tables)
    {
        // Canonical relational structure, including constraints, not a version string chosen
        // by an archive author. Provider store types deliberately prevent cross-provider imports.
        var fullModel = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var schema = tables.Select(table => fullModel.FindTable(table.Name, table.Schema)!).Select(t => new
        {
            t.Name, t.Schema, Columns = Describe(t).Columns,
            Keys = t.UniqueConstraints.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new { x.Name, Columns = x.Columns.Select(c => c.Name).ToArray() }).ToArray(),
            ForeignKeys = t.ForeignKeyConstraints.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new { x.Name, Columns = x.Columns.Select(c => c.Name).ToArray(), Principal = x.PrincipalTable.Name, x.PrincipalTable.Schema, PrincipalColumns = x.PrincipalColumns.Select(c => c.Name).ToArray(), x.OnDeleteAction }).ToArray(),
            Indexes = t.Indexes.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new { x.Name, x.IsUnique, Columns = x.Columns.Select(c => c.Name).ToArray() }).ToArray(),
            Checks = t.CheckConstraints.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new { x.Name, x.Sql }).ToArray()
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schema)));
    }

    private string Quote(string identifier) => db.GetService<ISqlGenerationHelper>().DelimitIdentifier(identifier);
    private string Quote(ITable table) => db.GetService<ISqlGenerationHelper>().DelimitIdentifier(table.Name, table.Schema);
    private DbCommand Command(string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql; command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }
    private static void Limit() => throw new RecoveryOperationException("This instance exceeds the bounded recovery archive limits (128 MiB, 100,000 rows, 1,000,000 cells).");
}
