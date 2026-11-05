using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Operations;

public sealed class AgentDisconnectEntity
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid OperationId { get; set; }
    public long At { get; set; }
    public string Reason { get; set; } = "";
    public int? CloseStatus { get; set; }
    public string Phase { get; set; } = "unknown";
    public long LastHeartbeat { get; set; }
    public long AcknowledgedSequence { get; set; }
}
public sealed class AgentDisconnectConfiguration : IEntityTypeConfiguration<AgentDisconnectEntity>
{
    public void Configure(EntityTypeBuilder<AgentDisconnectEntity> builder)
    { builder.ToTable("AgentDisconnects").HasKey(x => x.Id); builder.HasIndex(x => x.OperationId); }
}
