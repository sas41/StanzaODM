using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Transport;
using System.Text.Json;

namespace Stanza.ControlPlane.Operations;

public sealed class OperationOutputEntity
{
    public Guid OperationId { get; set; }
    public long Sequence { get; set; }
    public string Stream { get; set; } = "";
    public string ProtectedText { get; set; } = "";
}

public sealed class OperationOutputConfiguration : IEntityTypeConfiguration<OperationOutputEntity>
{
    public void Configure(EntityTypeBuilder<OperationOutputEntity> builder)
    {
        builder.ToTable("OperationOutput").HasKey(x => new { x.OperationId, x.Sequence });
        builder.HasOne<OperationEntity>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OperationOutputStore(StanzaDbContext db, IDataProtectionProvider protection, IAgentOutputArchive? archive = null)
{
    private IDataProtector Protector(Guid id, long sequence) => protection.CreateProtector("Stanza.OperationOutput.v1", id.ToString("N"), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task AppendAsync(Guid id, AgentOutputChunk chunk, CancellationToken ct = default)
    {
        var protectedText = Protector(id, chunk.Sequence).Protect(chunk.Text);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OperationOutput" ("OperationId", "Sequence", "Stream", "ProtectedText")
            VALUES ({id}, {chunk.Sequence}, {chunk.Stream}, {protectedText})
            ON CONFLICT ("OperationId", "Sequence") DO NOTHING
            """, ct);
        var existing = await db.Set<OperationOutputEntity>().AsNoTracking().SingleAsync(x => x.OperationId == id && x.Sequence == chunk.Sequence, ct);
        if (existing.Stream != chunk.Stream || Protector(id, chunk.Sequence).Unprotect(existing.ProtectedText) != chunk.Text)
            throw new IOException("Replayed output differs from retained output.");
    }

    public async Task<long> LastSequenceAsync(Guid id, CancellationToken ct = default) =>
        await db.Set<OperationOutputEntity>().Where(x => x.OperationId == id).Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;

    public async Task<IReadOnlyList<AgentOutputChunk>> ReadAsync(Guid id, long after, int count = 64, CancellationToken ct = default)
    {
        var rows = await db.Set<OperationOutputEntity>().AsNoTracking().Where(x => x.OperationId == id && x.Sequence > after)
            .OrderBy(x => x.Sequence).Take(count).ToListAsync(ct);
        if (rows.Count > 0) return rows.Select(x => new AgentOutputChunk(x.Sequence, x.Stream, Protector(id, x.Sequence).Unprotect(x.ProtectedText))).ToArray();
        if (archive is null) return [];
        var operation = await db.Set<OperationEntity>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.AgentId, x.State, x.ProtectedResponse }).SingleOrDefaultAsync(ct);
        if (operation is null || operation.State is "Succeeded" or "Failed" or "Cancelled") return [];
        if (operation.ProtectedResponse is { } stored)
        {
            var response = JsonSerializer.Deserialize<AgentWorkResponse>(protection.CreateProtector("Stanza.Operation.v1", id.ToString("N")).Unprotect(stored));
            if (response is { ExecutionStopped: true } or { Succeeded: true }) return [];
        }
        try { return await archive.ReadOutputAsync(operation.AgentId, id, after, count, ct); }
        catch (AgentUnavailableException) { return []; }
    }

    public Task WaitAsync(Guid id, CancellationToken ct) => archive?.WaitForOutputAsync(id, ct) ?? Task.Delay(200, ct);

    public async Task ArchiveAsync(Guid agentId, Guid id, long lastSequence, CancellationToken ct)
    {
        if (archive is null) return;
        var cursor = await LastSequenceAsync(id, ct);
        if (cursor > lastSequence || await db.Set<OperationOutputEntity>().LongCountAsync(x => x.OperationId == id, ct) != cursor)
            throw new IOException("Persisted output is not a contiguous prefix of the Agent archive.");
        while (cursor < lastSequence)
        {
            var chunks = await archive.ReadOutputAsync(agentId, id, cursor, (int)Math.Min(64, lastSequence - cursor), ct);
            if (chunks.Count == 0 || chunks.Where((chunk, index) => chunk.Sequence != cursor + index + 1).Any() || chunks[^1].Sequence > lastSequence)
                throw new IOException("The Agent archive is incomplete.");
            var rows = chunks.Select(chunk => new OperationOutputEntity { OperationId = id, Sequence = chunk.Sequence,
                Stream = chunk.Stream, ProtectedText = Protector(id, chunk.Sequence).Protect(chunk.Text) }).ToArray();
            db.AddRange(rows);
            try { await db.SaveChangesAsync(ct); }
            finally { foreach (var row in rows) db.Entry(row).State = EntityState.Detached; }
            cursor = chunks[^1].Sequence;
        }
    }

    public async Task<bool> CanReleaseAsync(Guid agentId, AgentJobInspection job, CancellationToken ct)
    {
        if (!job.Stopped || job.Response is null || job.LastSequence < 0) return false;
        var row = await db.Set<OperationEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.Id && x.AgentId == agentId, ct);
        if (row?.ProtectedResponse is null || row.State is "Queued" or "Running" or "CancellationRequested") return false;
        var receipt = JsonSerializer.Deserialize<AgentWorkResponse>(protection.CreateProtector("Stanza.Operation.v1", job.Id.ToString("N")).Unprotect(row.ProtectedResponse));
        return receipt is not null && (receipt.ExecutionStopped || receipt.Succeeded) && receipt.Id == job.Id &&
            receipt.Code == job.Response.Code && receipt.Succeeded == job.Response.Succeeded && receipt.LastOutputSequence == job.LastSequence &&
            await LastSequenceAsync(job.Id, ct) == job.LastSequence &&
            await db.Set<OperationOutputEntity>().LongCountAsync(x => x.OperationId == job.Id, ct) == job.LastSequence;
    }
}
