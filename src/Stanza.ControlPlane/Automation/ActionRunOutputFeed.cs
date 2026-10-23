using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

namespace Stanza.ControlPlane.Automation;

public sealed record ActionOperationFeed(Guid Id, int? Step, string State, string Stage, string? Code, bool OutputAllowed,
    IReadOnlyList<AgentOutputChunk> Chunks, string? Output)
{
    public override string ToString() => $"Operation feed {Id} (output redacted)";
}
public sealed record ActionOutputBatch(IReadOnlyList<ActionOperationFeed> Operations, bool HasMore);

/// <summary>Run visibility never implies permission to disclose the underlying operation's output.</summary>
public sealed class ActionRunOutputFeed(StanzaDbContext db, OperationService operations, OperationOutputStore output)
{
    public async Task<ActionOutputBatch> ReadAsync(Guid actor, AutomationRunDetails detail, IDictionary<Guid, long> cursors,
        ISet<Guid> legacySent, CancellationToken ct)
    {
        var result = new List<ActionOperationFeed>(); var remaining = 64; var more = false;
        var targets = detail.Steps.Where(x => x.OperationId.HasValue).Select(x => (Id: x.OperationId!.Value, Step: (int?)(x.Index + 1))).ToList();
        if (detail.Run.OperationId is { } current && targets.All(x => x.Id != current)) targets.Add((current, null));
        foreach (var (id, step) in targets.DistinctBy(x => x.Id))
        {
            var state = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.Id == id).Select(x => new { x.State, x.Stage, x.Code, HasResponse = x.ProtectedResponse != null }).SingleOrDefaultAsync(ct);
            if (state is null) continue;
            var allowed = await operations.CanReadOutputAsync(actor, id, ct);
            var after = cursors.TryGetValue(id, out var value) ? value : 0;
            IReadOnlyList<AgentOutputChunk> chunks = [];
            string? legacy = null;
            if (allowed)
            {
                if (remaining > 0)
                {
                    var count = Math.Min(8, remaining);
                    chunks = await output.ReadAsync(id, after, count, ct);
                    more |= chunks.Count == count;
                    if (chunks.Count > 0) cursors[id] = chunks[^1].Sequence;
                    remaining -= chunks.Count;
                    if (state.HasResponse && after == 0 && chunks.Count == 0 && legacySent.Add(id)) legacy = (await operations.DetailAsync(actor, id, ct)).Output;
                }
                else more = true;
            }
            result.Add(new(id, step, state.State, state.Stage, state.Code, allowed, chunks, legacy));
        }
        return new(result, more);
    }
}
