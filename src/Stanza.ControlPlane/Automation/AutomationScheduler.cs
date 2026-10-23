using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Automation;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;

namespace Stanza.ControlPlane.Automation;

public sealed class AutomationScheduler(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization, TimeProvider clock)
{
    public async Task TickAsync(CancellationToken ct = default)
    {
        var due = await db.Set<AutomationSchedule>().AsNoTracking().Where(x => x.Enabled && x.NextRunAt <= automation.Now)
            .OrderBy(x => x.NextRunAt).Take(100).ToListAsync(ct);
        foreach (var schedule in due) await ClaimScheduleAsync(schedule, ct);
        var policies = await db.Set<ImageUpdatePolicy>().AsNoTracking().Where(x => x.Enabled && x.NextRunAt <= automation.Now)
            .OrderBy(x => x.NextRunAt).Take(100).ToListAsync(ct);
        foreach (var policy in policies) await ClaimImageCheckAsync(policy, ct);
        await RecoverExpiredAsync(ct);
        await RetainAsync(ct);
    }

    public async Task<bool> ClaimScheduleAsync(AutomationSchedule schedule, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!schedule.Enabled || schedule.NextRunAt > automation.Now) return false;
        await SecurityWriteGuard.LockAsync(db, ct);
        long next;
        try { next = new CronSchedule(schedule.Expression).Next(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone)).ToUnixTimeSeconds(); }
        catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            await db.Set<AutomationSchedule>().Where(x => x.Id == schedule.Id && x.Revision == schedule.Revision)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, false).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            await transaction.CommitAsync(ct); return false;
        }
        // Advancing next occurrence and inserting the unique occurrence ledger share a transaction.
        // A crash before commit claims neither; a crash after commit leaves exactly one durable run.
        var updated = await db.Set<AutomationSchedule>().Where(x => x.Id == schedule.Id && x.Revision == schedule.Revision && x.Enabled && x.NextRunAt == schedule.NextRunAt)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAt, next).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        if (updated != 1) return false;
        await db.Set<AutomationQueueGuard>().Where(x => x.Id == 1).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        var state = "queued"; var code = "";
        IReadOnlyDictionary<string, string>? parameters = null;
        if (schedule.MissedRun == "skip" && schedule.NextRunAt < automation.Now - 60) { state = "skipped"; code = "missed-occurrence"; }
        else if (schedule.Overlap == "skip" && await db.Set<AutomationRun>().AnyAsync(x => x.DefinitionId == schedule.Id && (x.State == "queued" || x.State == "running"), ct))
        { state = "skipped"; code = "overlap"; }
        try
        {
            await authorization.ScheduleAsync(schedule.OwnerId, schedule.ActionId, schedule.DeploymentId, ct);
            await automation.ValidateTriggerAsync(schedule.OwnerId, schedule.ActionId, schedule.DeploymentId, schedule.Operation, ct);
            if (state == "queued" && schedule.ActionId is { } actionId) parameters = await automation.ScheduledParametersAsync(actionId, ct);
        }
        catch (AutomationAccessException) { state = "denied"; code = "access-revoked"; }
        catch (AutomationException) { state = "denied"; code = "target-or-inputs-unavailable"; }
        var key = $"schedule:{schedule.Id:N}:{schedule.NextRunAt}";
        try
        {
            await automation.QueueAsync(schedule.OwnerId, schedule.DeploymentId, schedule.ActionId, schedule.Id, "schedule",
                key, schedule.Operation, parameters, state: state, code: code, ct: ct);
        }
        catch (Exception e) when (state == "queued" && e is AutomationException or AutomationAccessException or SourceException or SourceAccessException or AgentUnavailableException)
        {
            // A removed branch, denied registry, or full queue must record this occurrence and
            // advance, rather than retry the same destructive job on every scheduler tick.
            await automation.QueueAsync(schedule.OwnerId, schedule.DeploymentId, schedule.ActionId, schedule.Id, "schedule",
                key, schedule.Operation, state: "denied", code: "queue-or-inputs-unavailable", ct: ct);
        }
        await transaction.CommitAsync(ct); return true;
    }

    private async Task ClaimImageCheckAsync(ImageUpdatePolicy policy, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        var next = automation.Now + policy.FrequencyMinutes * 60L;
        if (await db.Set<ImageUpdatePolicy>().Where(x => x.Id == policy.Id && x.Enabled && x.Revision == policy.Revision && x.NextRunAt == policy.NextRunAt)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAt, next).SetProperty(x => x.Revision, x => x.Revision + 1), ct) != 1) return;
        await db.Set<AutomationQueueGuard>().Where(x => x.Id == 1).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        var overlap = await db.Set<AutomationRun>().AnyAsync(x => x.DefinitionId == policy.Id && (x.State == "running" || x.State == "queued"), ct);
        await automation.QueueAsync(policy.OwnerId, policy.DeploymentId, null, policy.Id, "image-policy", $"image:{policy.Id:N}:{policy.NextRunAt}",
            "image-check", state: overlap ? "skipped" : "queued", code: overlap ? "overlap" : "", ct: ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecoverExpiredAsync(CancellationToken ct = default)
    {
        var expired = await db.Set<AutomationRun>().AsNoTracking().Where(x => x.State == "running" && x.LeaseUntil < automation.Now).Take(100).ToListAsync(ct);
        foreach (var run in expired)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.Set<AutomationRun>().Where(x => x.Id == run.Id && x.State == "running" && x.LeaseUntil < automation.Now)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "uncertain").SetProperty(x => x.Code, "worker-interrupted-no-replay")
                    .SetProperty(x => x.CompletedAt, automation.Now), ct) == 1)
            {
                // Keep the action/policy lock on uncertain work: an unknown host/deploy result must
                // not admit another conflicting run. An operator must reconcile/cancel first.
                await db.Set<AutomationStepRun>().Where(x => x.RunId == run.Id && x.State == "running")
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "uncertain").SetProperty(x => x.Code, "worker-interrupted-no-replay"), ct);
            }
            await transaction.CommitAsync(ct);
        }
    }

    private async Task RetainAsync(CancellationToken ct)
    {
        var limits = await automation.LimitsAsync(ct); var cutoff = automation.Now - limits.HistoryRetentionDays * 86400L;
        if (limits.HistoryRetentionDays == 0) return;
        await db.Set<WebhookDelivery>().Where(x => x.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        // Uncertain runs and active locks are never pruned automatically.
        await db.Set<AutomationRun>().Where(x => x.CompletedAt < cutoff && x.State != "uncertain").ExecuteDeleteAsync(ct);
    }
}
