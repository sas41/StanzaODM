using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Operations;

public sealed class OperationEntity
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid AgentId { get; set; }
    public bool RequiresMovePermission { get; set; }
    public long Revision { get; set; }
    public string Kind { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string Stage { get; set; } = "Queued";
    public string Trigger { get; set; } = "manual";
    public string ProtectedRequest { get; set; } = "";
    public string? ProtectedResponse { get; set; }
    public string? ProtectedAppliedMetadata { get; set; }
    public Guid? ParentAppliedOperationId { get; set; }
    public string? Code { get; set; }
    public long CreatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public long LeaseUntil { get; set; }
    public Guid? WorkerId { get; set; }
}
public sealed class DeploymentRuntimeEntity
{
    public Guid DeploymentId { get; set; }
    public Guid AgentId { get; set; }
    public long Revision { get; set; }
    public string DesiredState { get; set; } = "unknown";
    public string ObservedState { get; set; } = "unknown";
    public long ObservedAt { get; set; }
}
public sealed class OperationConfiguration : IEntityTypeConfiguration<OperationEntity>
{
    public void Configure(EntityTypeBuilder<OperationEntity> builder)
    {
        builder.ToTable("Operations").HasKey(x => x.Id);
        builder.HasIndex(x => new { x.State, x.CreatedAt });
        builder.HasIndex(x => x.DeploymentId);
        builder.Property(x => x.State).HasMaxLength(40);
        builder.Property(x => x.Kind).HasMaxLength(40);
        builder.Property(x => x.Code).HasMaxLength(100);
        builder.Property(x => x.Trigger).HasMaxLength(100);
    }
}
public sealed class RuntimeConfiguration : IEntityTypeConfiguration<DeploymentRuntimeEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentRuntimeEntity> builder) => builder.ToTable("DeploymentRuntime").HasKey(x => x.DeploymentId);
}
