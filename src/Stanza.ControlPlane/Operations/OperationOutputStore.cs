using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

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

public sealed class OperationOutputStore(StanzaDbContext db, IDataProtectionProvider protection)
{
    private IDataProtector Protector(Guid id, long sequence) => protection.CreateProtector("Stanza.OperationOutput.v1", id.ToString("N"), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task AppendAsync(Guid id, AgentOutputChunk chunk, CancellationToken ct = default)
    {
        db.Set<OperationOutputEntity>().Add(new() { OperationId = id, Sequence = chunk.Sequence, Stream = chunk.Stream,
            ProtectedText = Protector(id, chunk.Sequence).Protect(chunk.Text) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentOutputChunk>> ReadAsync(Guid id, long after, int count = 64, CancellationToken ct = default)
    {
        var rows = await db.Set<OperationOutputEntity>().AsNoTracking().Where(x => x.OperationId == id && x.Sequence > after)
            .OrderBy(x => x.Sequence).Take(count).ToListAsync(ct);
        return rows.Select(x => new AgentOutputChunk(x.Sequence, x.Stream, Protector(id, x.Sequence).Unprotect(x.ProtectedText))).ToArray();
    }
}
