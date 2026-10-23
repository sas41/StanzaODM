using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Configuration;

public sealed class AgentPolicyEntity
{
    public Guid AgentId { get; set; }
    public string Json { get; set; } = "";
    public long Version { get; set; }
}
public sealed class AgentPolicyConfiguration : IEntityTypeConfiguration<AgentPolicyEntity>
{
    public void Configure(EntityTypeBuilder<AgentPolicyEntity> builder) => builder.ToTable("AgentPolicies").HasKey(x => x.AgentId);
}
public sealed record AgentPolicyEditor(Guid AgentId, AgentRuntimePolicy Policy, long Version);
public sealed class AgentPolicyService(StanzaDbContext db, AccessService access, OperationalPolicyService operational)
{
    public async Task<AgentRuntimePolicy> RuntimeAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Set<AgentPolicyEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == id, ct);
        var global = await operational.RuntimeAsync(ct);
        return row is null ? global : (JsonSerializer.Deserialize<AgentRuntimePolicy>(row.Json) ?? throw new SettingsOperationException("Invalid Agent runtime policy.")) with { TimeoutSeconds = global.TimeoutSeconds };
    }
    public async Task<AgentPolicyEditor> ReadAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await RequireAsync(actor, "agent.read", id, ct);
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == id, ct)) throw new SettingsOperationException("Agent not found.");
        var version = await db.Set<AgentPolicyEntity>().Where(x => x.AgentId == id).Select(x => (long?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
        return new(id, await RuntimeAsync(id, ct), version);
    }
    public async Task SaveAsync(Guid actor, AgentPolicyEditor edit, CancellationToken ct = default)
    {
        var p = edit.Policy with { TimeoutSeconds = (await operational.RuntimeAsync(ct)).TimeoutSeconds };
        if (!Uri.TryCreate(p.DockerEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "unix"
            || endpoint.AbsolutePath == "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || p.DockerEndpoint.Any(char.IsControl))
            throw new SettingsOperationException("Use a local unix Docker socket and bounded timeout/output limits.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        await RequireAsync(actor, "agent.manage", edit.AgentId, ct);
        if (!await db.Set<AgentEntity>().AnyAsync(x => x.Id == edit.AgentId, ct)) throw new SettingsOperationException("Agent not found.");
        var currentVersion = await db.Set<AgentPolicyEntity>().AsNoTracking().Where(x => x.AgentId == edit.AgentId).Select(x => (long?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
        if (currentVersion != edit.Version) throw new SettingsOperationException("Agent policy changed. Reload before saving.");
        var row = await db.Set<AgentPolicyEntity>().SingleOrDefaultAsync(x => x.AgentId == edit.AgentId, ct);
        if (row is null) { row = new() { AgentId = edit.AgentId }; db.Set<AgentPolicyEntity>().Add(row); }
        row.Json = JsonSerializer.Serialize(p); row.Version = checked(edit.Version + 1);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    private async Task RequireAsync(Guid actor, string action, Guid id, CancellationToken ct)
    { if (!await access.IsAllowedAsync(actor, action, "agent", id, ct)) throw new SettingsOperationException("Agent permission required: " + action); }
}
