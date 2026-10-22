using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Deployments;

public sealed class DeploymentDraftEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? TargetAgentId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeploymentDraftRevisionEntity
{
    public Guid DeploymentId { get; set; }
    public long Number { get; set; }
    public Guid SavedBy { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    [JsonIgnore] public byte[] ProtectedContent { get; set; } = [];
}

public sealed class DeploymentDraftEntityConfiguration : IEntityTypeConfiguration<DeploymentDraftEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentDraftEntity> builder)
    {
        builder.ToTable("DeploymentDrafts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => x.Name);
        // Drafts may be unassigned; the application enforces approval and agent.read on assignment.
    }
}

public sealed class DeploymentDraftRevisionEntityConfiguration : IEntityTypeConfiguration<DeploymentDraftRevisionEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentDraftRevisionEntity> builder)
    {
        builder.ToTable("DeploymentDraftRevisions");
        builder.HasKey(x => new { x.DeploymentId, x.Number });
        builder.Property(x => x.ProtectedContent).IsRequired();
        builder.HasOne<DeploymentDraftEntity>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}
