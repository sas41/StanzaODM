using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Automation;

public sealed record ActionDeletionTrigger(Guid Id, string Name, long Revision);
public sealed record ActionDeletionDetails(Guid Id, string Name, long Revision,
    IReadOnlyList<ActionDeletionTrigger> Schedules, IReadOnlyList<ActionDeletionTrigger> Webhooks,
    string Fingerprint, string? BlockedReason);

public sealed class ActionDeletionService(StanzaDbContext db, AutomationService automation, AutomationAuthorization authorization)
{
    public async Task<ActionDeletionDetails> PreviewAsync(Guid actor, Guid id, CancellationToken ct = default)
    {
        await authorization.RequireAsync(actor, "action.manage", "action", id, ct);
        var action = await db.Set<ActionDefinition>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AutomationException("Action is unavailable.");
        var schedules = await db.Set<AutomationSchedule>().AsNoTracking().Where(x => x.ActionId == id)
            .OrderBy(x => x.Id).Select(x => new ActionDeletionTrigger(x.Id, x.Name, x.Revision)).ToArrayAsync(ct);
        var hooks = await db.Set<WebhookDefinition>().AsNoTracking().Where(x => x.ActionId == id).OrderBy(x => x.Id).ToArrayAsync(ct);
        // Managing the action does not confer authority to remove another deployment's webhooks.
        foreach (var hook in hooks)
            await authorization.RequireAsync(actor, "automation.manage", "deployment", hook.DeploymentId, ct);
        var webhooks = hooks.Select(x => new ActionDeletionTrigger(x.Id, x.Name, x.Revision)).ToArray();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { action.Id, action.Revision, Schedules = schedules, Webhooks = webhooks })));
        var runs = db.Set<AutomationRun>().Where(x => x.ActionId == id);
        var runIds = runs.Select(x => x.Id);
        var stepOperations = db.Set<AutomationStepRun>().Where(x => runIds.Contains(x.RunId)).Select(x => x.OperationId);
        var runOperations = runs.Select(x => x.OperationId);
        var blocked = action.ActiveRunId is not null ||
            await runs.AnyAsync(x => x.State == "queued" || x.State == "running" || x.State == "uncertain", ct) ||
            await db.Set<OperationEntity>().AnyAsync(x => (stepOperations.Contains(x.Id) || runOperations.Contains(x.Id)) &&
                (x.State == "Queued" || x.State == "Running" || x.State == "CancellationRequested" || x.State == "Unknown"), ct);
        return new(id, action.Name, action.Revision, schedules, webhooks, fingerprint, blocked
            ? "This action has queued, running, or unresolved work. Cancel queued work, wait for running work, and reconcile uncertain results before deleting it. Deletion never clears execution locks."
            : null);
    }

    public async Task DeleteAsync(Guid actor, Guid id, long revision, string? fingerprint, bool confirmed,
        bool deleteLinkedTriggers, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize against queueing, claims, configuration edits and security changes. Recheck
        // the complete dependency snapshot under this lock, not the earlier confirmation page.
        await SecurityWriteGuard.LockAsync(db, ct);
        var details = await PreviewAsync(actor, id, ct);
        if (!confirmed) throw new AutomationException("Confirm that you want to delete this action.");
        if (details.Revision != revision || !string.Equals(details.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new AutomationException("The action or its linked triggers changed. Review the current deletion confirmation before trying again.");
        if (details.BlockedReason is { } blocked) throw new AutomationException(blocked);
        if ((details.Schedules.Count > 0 || details.Webhooks.Count > 0) && !deleteLinkedTriggers)
            throw new AutomationException("Separately confirm removal of the listed schedules and webhooks, or reassign them before deleting this action.");

        var grants = db.Grants.Where(x => x.ResourceType == "action" && x.ResourceId == id);
        var directAccounts = grants.Where(x => x.AccountId != null).Select(x => x.AccountId);
        var groups = grants.Where(x => x.GroupId != null).Select(x => x.GroupId);
        var stamp = Guid.NewGuid().ToString("N");
        await db.Accounts.Where(x => directAccounts.Contains(x.Id) || db.GroupMemberships.Any(m => m.AccountId == x.Id && groups.Contains(m.GroupId)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, stamp), ct);
        await grants.ExecuteDeleteAsync(ct);
        await db.Set<AutomationSchedule>().Where(x => x.ActionId == id).ExecuteDeleteAsync(ct);
        await db.Set<WebhookDefinition>().Where(x => x.ActionId == id).ExecuteDeleteAsync(ct);
        await db.Set<ActionInputMemory>().Where(x => x.ActionId == id).ExecuteDeleteAsync(ct);
        if (await db.Set<ActionDefinition>().Where(x => x.Id == id && x.Revision == revision && x.ActiveRunId == null).ExecuteDeleteAsync(ct) != 1)
            throw new AutomationException("The action changed. Reload before deleting it.");
        // Runs, steps, operation output and webhook delivery history deliberately have no
        // cascading action-definition foreign key. Never delete or rewrite that evidence.
        await automation.RecordAuditAsync(actor, "automation.action.deleted", id.ToString(),
            $"deleted;schedules:{details.Schedules.Count};webhooks:{details.Webhooks.Count};history-retained", ct);
        await transaction.CommitAsync(ct);
    }
}
