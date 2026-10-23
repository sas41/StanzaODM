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
    [Range(0, 168)] public int SessionHours { get; set; } = 8;
    [Range(0, int.MaxValue)] public int LoginAttempts { get; set; } = 20;
    [Range(0, 35791394)] public int LoginWindowMinutes { get; set; } = 5;
    [Range(0, int.MaxValue)] public int EnrollmentRequestsPerMinute { get; set; } = 120;
    [Range(0, 604800)] public int OperationTimeoutSeconds { get; set; } = 3600;
    [Range(0, int.MaxValue)] public int MaximumParallelOperations { get; set; } = 4;
    [Range(0, int.MaxValue)] public int MaximumTextBytes { get; set; } = 4 * 1024 * 1024;
    [Range(0, int.MaxValue)] public int MaximumUploadBytes { get; set; } = 16 * 1024 * 1024;
    [Range(0, int.MaxValue)] public int MaximumWorkspaceBytes { get; set; } = 64 * 1024 * 1024;
    [Range(0, int.MaxValue)] public int MaximumSupportingFiles { get; set; } = 128;
    [Range(0, 604800)] public int StatusIntervalSeconds { get; set; } = 15;
    [Range(0, 604800)] public int ConfigurationIntervalSeconds { get; set; } = 15;
    [Range(0, 604800)] public int StaleStatusSeconds { get; set; } = 15;
    [Range(0, int.MaxValue)] public int MaximumConnectionsPerUser { get; set; } = 4;
    [Range(0, int.MaxValue)] public int MaximumConnections { get; set; } = 1024;
    [Range(0, 3650)] public int LogRetentionDays { get; set; } = 30;
    [Range(0, 3650)] public int AuditRetentionDays { get; set; } = 365;
    public bool AllowHostActions { get; set; }
    [StringLength(4096)] public string TrustedProxyAddresses { get; set; } = "127.0.0.0/8,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,::1,fc00::/7";
    [Range(0, int.MaxValue)] public int TrustedProxyForwardLimit { get; set; } = 1;
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
        edit.Policy.TrustedProxyAddresses ??= "";
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(edit.Policy, new ValidationContext(edit.Policy), errors, true))
            throw new SettingsOperationException("Operational limits are invalid or inconsistent.");
        TrustedProxyPolicy.Parse(edit.Policy.TrustedProxyAddresses);
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
        return new(true, policy.OperationTimeoutSeconds);
    }
}
