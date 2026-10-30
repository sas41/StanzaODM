using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Deployments;

public sealed class DeploymentLock
{
    public Guid DeploymentId { get; set; }
    public Guid LockedBy { get; set; }
    public string Username { get; set; } = "";
    public string Description { get; set; } = "";
    public long ExpiresAt { get; set; }
}

public sealed class DeploymentLockConfiguration : IEntityTypeConfiguration<DeploymentLock>
{
    public void Configure(EntityTypeBuilder<DeploymentLock> b)
    {
        b.ToTable("DeploymentLocks").HasKey(x => x.DeploymentId);
        b.HasOne<DeploymentDraftEntity>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed record DeploymentLockView(DeploymentLock? Current, int MaximumHours, bool CanLock, bool CanUnlock);

public sealed class DeploymentLockService(StanzaDbContext db, AccessService access, OperationalPolicyService policies, TimeProvider clock)
{
    public static Task<bool> IsLockedAsync(StanzaDbContext db, Guid deployment, long now, CancellationToken ct = default) =>
        db.Set<DeploymentLock>().AnyAsync(x => x.DeploymentId == deployment && x.ExpiresAt > now, ct);

    private Task<bool> IsAdmin(Guid actor, CancellationToken ct) => db.Accounts.AnyAsync(x => x.Id == actor && x.Enabled &&
        (x.Role == AccountRole.Admin || x.Role == AccountRole.SystemAdmin), ct);

    public async Task<DeploymentLockView> ReadAsync(Guid actor, Guid deployment, CancellationToken ct = default)
    {
        if (!await access.IsAllowedAsync(actor, "deployment.read", "deployment", deployment, ct)) throw new DeploymentDraftAccessException();
        if (!await db.Set<DeploymentDraftEntity>().AnyAsync(x => x.Id == deployment, ct)) throw new DeploymentDraftException("Deployment is unavailable.");
        var current = await db.Set<DeploymentLock>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds(), ct);
        return new(current, (await policies.GetAsync(ct)).Policy.MaximumDeploymentLockHours,
            await access.IsAllowedAsync(actor, "deployment.deploy", "deployment", deployment, ct),
            current is not null && (current.LockedBy == actor || await IsAdmin(actor, ct)));
    }

    public async Task LockAsync(Guid actor, Guid deployment, int hours, string? description, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var view = await ReadAsync(actor, deployment, ct);
        if (!view.CanLock) throw new DeploymentDraftAccessException();
        if (hours < 1 || view.MaximumHours > 0 && hours > view.MaximumHours) throw new DeploymentDraftException($"Lock duration must be positive and within the configured maximum ({view.MaximumHours}; 0 means no maximum).");
        long expiresAt;
        try { expiresAt = clock.GetUtcNow().AddHours(hours).ToUnixTimeSeconds(); }
        catch (ArgumentOutOfRangeException) { throw new DeploymentDraftException("The lock duration exceeds the supported date range."); }
        if (description?.Length > 1000) throw new DeploymentDraftException("Lock description must be at most 1,000 characters.");
        if (view.Current is not null) throw new DeploymentDraftException("This deployment is already locked. Unlock it before changing the lock.");
        var row = await db.Set<DeploymentLock>().SingleOrDefaultAsync(x => x.DeploymentId == deployment, ct);
        if (row is null) { row = new() { DeploymentId = deployment }; db.Add(row); }
        row.LockedBy = actor; row.Username = await db.Accounts.Where(x => x.Id == actor).Select(x => x.Username).SingleAsync(ct);
        row.Description = description ?? ""; row.ExpiresAt = expiresAt;
        db.Add(new Audit.AuditEvent { Id = Guid.NewGuid(), ActorId = actor, Action = "deployment.locked", Resource = deployment.ToString(), Result = hours.ToString(), At = clock.GetUtcNow().ToUnixTimeSeconds() });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        db.Entry(row).State = EntityState.Detached;
    }

    public async Task UnlockAsync(Guid actor, Guid deployment, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var view = await ReadAsync(actor, deployment, ct);
        if (view.Current is null) return;
        if (!view.CanUnlock) throw new DeploymentDraftAccessException();
        await db.Set<DeploymentLock>().Where(x => x.DeploymentId == deployment).ExecuteDeleteAsync(ct);
        foreach (var entry in db.ChangeTracker.Entries<DeploymentLock>().Where(x => x.Entity.DeploymentId == deployment).ToArray()) entry.State = EntityState.Detached;
        db.Add(new Audit.AuditEvent { Id = Guid.NewGuid(), ActorId = actor, Action = "deployment.unlocked", Resource = deployment.ToString(), Result = "released", At = clock.GetUtcNow().ToUnixTimeSeconds() });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
}
