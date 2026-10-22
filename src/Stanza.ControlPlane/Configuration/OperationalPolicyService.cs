using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Configuration;

public sealed record OperationalPolicy
{
    [Range(1, 168)] public int SessionHours { get; set; } = 8;
    [Range(1, 1000)] public int LoginAttempts { get; set; } = 20;
    [Range(1, 60)] public int LoginWindowMinutes { get; set; } = 5;
    [Range(1, 10000)] public int EnrollmentRequestsPerMinute { get; set; } = 120;
    [Range(1, 3600)] public int OperationTimeoutSeconds { get; set; } = 300;
    [Range(1024, 1048576)] public int MaximumOutputCharacters { get; set; } = 65536;
    [Range(1, 32)] public int MaximumParallelOperations { get; set; } = 4;
    [Range(1, 1048576)] public int MaximumTextBytes { get; set; } = 1048576;
    [Range(1, 4194304)] public int MaximumUploadBytes { get; set; } = 4194304;
    [Range(1, 16777216)] public int MaximumWorkspaceBytes { get; set; } = 16777216;
    [Range(1, 100)] public int MaximumSupportingFiles { get; set; } = 100;
    [Range(1, 60)] public int StatusIntervalSeconds { get; set; } = 15;
    [Range(1, 32)] public int MaximumConnectionsPerUser { get; set; } = 4;
    [Range(4, 10000)] public int MaximumConnections { get; set; } = 1024;
    [Range(1, 3650)] public int LogRetentionDays { get; set; } = 30;
    [Range(1, 3650)] public int AuditRetentionDays { get; set; } = 365;
    public bool AllowHostActions { get; set; }
}

public sealed record PolicyEditor(OperationalPolicy Policy, long Version);

public sealed class OperationalPolicyService(StanzaDbContext db, AccessService access)
{
    private const string Key = "operational-policy";

    public async Task<PolicyEditor> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == Key, ct);
        return row is null ? new(new(), 0) : JsonSerializer.Deserialize<PolicyEditor>(row.Value)
            ?? throw new SettingsOperationException("Operational settings could not be read.");
    }

    public async Task<PolicyEditor> ReadAsync(Guid actor, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actor, "settings.read", "instance", null, ct))
            throw new SettingsOperationException("Settings read access is required.");
        return await GetAsync(ct);
    }

    public async Task SaveAsync(Guid actor, PolicyEditor edit, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(edit.Policy, new ValidationContext(edit.Policy), errors, true)
            || edit.Policy.MaximumUploadBytes > edit.Policy.MaximumWorkspaceBytes
            || edit.Policy.MaximumTextBytes > edit.Policy.MaximumWorkspaceBytes
            || edit.Policy.MaximumConnectionsPerUser > edit.Policy.MaximumConnections)
            throw new SettingsOperationException("Operational limits are invalid or inconsistent.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        if (!await db.Accounts.AnyAsync(x => x.Id == actor && x.Enabled && (x.Role == AccountRole.Admin || x.Role == AccountRole.SystemAdmin), ct))
            throw new SettingsOperationException("An administrator must change security and execution policy.");
        var current = await GetAsync(ct);
        if (current.Version != edit.Version) throw new SettingsOperationException("Policy changed. Reload before saving.");
        var value = JsonSerializer.Serialize(new PolicyEditor(edit.Policy, checked(current.Version + 1)));
        var row = await db.Settings.SingleOrDefaultAsync(x => x.Key == Key, ct);
        if (row is null) db.Settings.Add(new() { Key = Key, Value = value }); else row.Value = value;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<AgentRuntimePolicy> RuntimeAsync(CancellationToken ct = default)
    {
        var policy = (await GetAsync(ct)).Policy;
        return new(true, policy.OperationTimeoutSeconds, policy.MaximumOutputCharacters);
    }
}
