using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Portability;

public sealed class PortableImportEntity
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public string Fingerprint { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string State { get; set; } = "preview";
    [JsonIgnore] public byte[] ProtectedContent { get; set; } = [];
    public string CreatedDeployments { get; set; } = "[]";
    public string CreatedResources { get; set; } = "{}";
    public string CompletedSections { get; set; } = "[]";
    public string? CurrentSection { get; set; }
    public string? Failure { get; set; }
}

public sealed class PortableMoveEntity
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid SourceAgentId { get; set; }
    public Guid DestinationAgentId { get; set; }
    public long Revision { get; set; }
    public string State { get; set; } = "validate";
    public Guid? OperationId { get; set; }
    public long LeaseUntil { get; set; }
    public string? Failure { get; set; }
    public long CreatedAt { get; set; }
    public long? RecoveryAt { get; set; }
    public string Warnings { get; set; } = "[]";
}

public sealed class PortableImportConfiguration : IEntityTypeConfiguration<PortableImportEntity>
{
    public void Configure(EntityTypeBuilder<PortableImportEntity> b)
    {
        b.ToTable("PortableImports").HasKey(x => x.Id);
        b.Property(x => x.State).HasMaxLength(32);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.ProtectedContent).IsRequired();
        b.HasIndex(x => new { x.ActorId, x.Fingerprint }).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class PortableMoveConfiguration : IEntityTypeConfiguration<PortableMoveEntity>
{
    public void Configure(EntityTypeBuilder<PortableMoveEntity> b)
    {
        b.ToTable("PortableMoves").HasKey(x => x.Id);
        b.Property(x => x.State).HasMaxLength(32);
        b.HasIndex(x => new { x.DeploymentId, x.CreatedAt });
    }
}
