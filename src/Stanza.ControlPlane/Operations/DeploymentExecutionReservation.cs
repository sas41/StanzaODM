using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Operations;

// Separate from user-created locks; held across every step and retained for uncertain outcomes.
public sealed class DeploymentExecutionReservation
{
    public Guid DeploymentId { get; set; }
    public Guid RunId { get; set; }
}

public sealed class DeploymentExecutionReservationConfiguration : IEntityTypeConfiguration<DeploymentExecutionReservation>
{
    public void Configure(EntityTypeBuilder<DeploymentExecutionReservation> b)
    {
        b.ToTable("DeploymentExecutionReservations").HasKey(x => x.DeploymentId);
        b.HasIndex(x => x.RunId);
    }
}
