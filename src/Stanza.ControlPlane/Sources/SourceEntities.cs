using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stanza.ControlPlane.Deployments;

namespace Stanza.ControlPlane.Sources;

public sealed class SourceCredentialEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "git";
    public string Endpoint { get; set; } = "";
    public long Version { get; set; }
    [JsonIgnore] public string ProtectedSecret { get; set; } = "";
}
public sealed class DeploymentGitSourceEntity
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public string Url { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Destination { get; set; } = "";
    public Guid? CredentialId { get; set; }
    public int Position { get; set; }
    public long Version { get; set; }
}
public sealed class DeploymentRegistryBindingEntity
{
    public Guid DeploymentId { get; set; }
    public Guid CredentialId { get; set; }
}
public sealed class SourceCredentialConfiguration : IEntityTypeConfiguration<SourceCredentialEntity>
{
    public void Configure(EntityTypeBuilder<SourceCredentialEntity> b)
    {
        b.ToTable("SourceCredentials").HasKey(x => x.Id);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Endpoint).HasMaxLength(2048);
        b.Property(x => x.Kind).HasMaxLength(16);
    }
}
public sealed class DeploymentGitSourceConfiguration : IEntityTypeConfiguration<DeploymentGitSourceEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentGitSourceEntity> b)
    {
        b.ToTable("DeploymentGitSources").HasKey(x => x.Id);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<DeploymentDraftEntity>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SourceCredentialEntity>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Url).HasMaxLength(2048);
        b.Property(x => x.Reference).HasMaxLength(256);
        b.Property(x => x.Destination).HasMaxLength(512);
    }
}
public sealed class DeploymentRegistryBindingConfiguration : IEntityTypeConfiguration<DeploymentRegistryBindingEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentRegistryBindingEntity> b)
    {
        b.ToTable("DeploymentRegistryBindings").HasKey(x => new { x.DeploymentId, x.CredentialId });
        b.HasOne<DeploymentDraftEntity>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SourceCredentialEntity>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
    }
}
