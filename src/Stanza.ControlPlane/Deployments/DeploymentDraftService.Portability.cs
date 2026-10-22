using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Deployments;

public sealed partial class DeploymentDraftService
{
    /// <summary>Creates one complete immutable encrypted revision. Creation grants do not imply read/edit grants on the new identity.</summary>
    internal async Task<DeploymentDraftSummary> CreatePortableWorkspaceAsync(Guid actorId, string name, Guid? targetAgentId,
        string composeText, string environmentText, IReadOnlyList<ManagedFileSnapshot> files,
        IReadOnlyList<GitSourceWorkspace>? sources = null, CancellationToken ct = default)
    {
        await GetEditorLimitsAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecurityWriteGuard.LockAsync(db, ct);
        if (!await access.IsAllowedAsync(actorId, "configuration.import", "instance", null, ct) || !await CanCreateAsync(actorId, ct))
            throw new DeploymentDraftAccessException();
        ValidateMetadata(name, targetAgentId);
        await ValidateTargetAsync(actorId, targetAgentId, ct);
        var payload = new DraftPayload { Name = name, TargetAgentId = targetAgentId, ComposeText = composeText,
            EnvironmentText = environmentText, Files = files.Select(StoredFile.From).ToList() };
        ValidatePayload(payload, sources);
        var now = clock.GetUtcNow();
        var draft = new DeploymentDraftEntity { Id = Guid.NewGuid(), Name = name, TargetAgentId = targetAgentId,
            Revision = 1, CreatedAt = now, UpdatedAt = now };
        var saved = Revision(draft.Id, 1, actorId, now, payload);
        db.Add(draft); db.Add(saved);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        finally { db.Entry(draft).State = EntityState.Detached; db.Entry(saved).State = EntityState.Detached; }
        return Summary(draft);
    }
}
