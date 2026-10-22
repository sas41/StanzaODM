using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.Core.Deployments;

namespace Stanza.ControlPlane.Deployments;

public sealed partial class DeploymentDraftService
{
    internal Task<DeploymentDraftSummary> ReassignPortablePlacementAsync(Guid actorId, Guid id, long expectedRevision,
        Guid agentId, CancellationToken ct = default) => MutateAsync(actorId, id, expectedRevision, async payload =>
        {
            // MutateAsync holds the shared security write guard through authorization, revision CAS and commit.
            await RequireAsync(actorId, "deployment.move", id, ct);
            if (!await access.IsAllowedAsync(actorId, "agent.manage", "agent", agentId, ct)) throw new DeploymentDraftAccessException();
            await ValidateTargetAsync(actorId, agentId, ct);
            payload.TargetAgentId = agentId;
        }, ct);

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
