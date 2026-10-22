using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Agents;

public sealed class AgentEntity
{
    public Guid Id { get; set; }
    public string Identity { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Status { get; set; } = "pending";
    public long RegisteredAt { get; set; }
    public long LastSeenAt { get; set; }
    public long Version { get; set; }
    public string ProtectedEnvironment { get; set; } = "";
}

public sealed class EnrollmentTokenEntity
{
    public bool FirstClaimPending { get; set; }
    public Guid Id { get; set; }
    public string Hash { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string? BoundIdentity { get; set; }
    public bool Revoked { get; set; }
}

public sealed class AgentNonceEntity
{
    public string Identity { get; set; } = "";
    public string Nonce { get; set; } = "";
    public long ExpiresAt { get; set; }
}

public sealed class AgentEntityConfiguration : IEntityTypeConfiguration<AgentEntity>
{
    public void Configure(EntityTypeBuilder<AgentEntity> builder)
    {
        builder.ToTable("Agents").HasKey(x => x.Id);
        builder.HasIndex(x => x.Identity).IsUnique();
        builder.Property(x => x.Identity).HasMaxLength(64);
        builder.Property(x => x.PublicKey).HasMaxLength(512);
        builder.Property(x => x.Hostname).HasMaxLength(253);
        builder.Property(x => x.Status).HasMaxLength(20);
        builder.Property(x => x.Version).IsConcurrencyToken();
    }
}

public sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentTokenEntity>
{
    public void Configure(EntityTypeBuilder<EnrollmentTokenEntity> builder)
    {
        builder.ToTable("AgentEnrollmentTokens").HasKey(x => x.Id);
        builder.HasIndex(x => x.Hash).IsUnique();
        builder.Property(x => x.Hash).HasMaxLength(64);
        builder.Property(x => x.BoundIdentity).HasMaxLength(64);
        builder.HasIndex(x => x.BoundIdentity).IsUnique();
    }
}

public sealed class AgentNonceConfiguration : IEntityTypeConfiguration<AgentNonceEntity>
{
    public void Configure(EntityTypeBuilder<AgentNonceEntity> builder)
    {
        builder.ToTable("AgentEnrollmentNonces").HasKey(x => new { x.Identity, x.Nonce });
        builder.Property(x => x.Identity).HasMaxLength(64);
        builder.Property(x => x.Nonce).HasMaxLength(44);
        builder.HasIndex(x => x.ExpiresAt);
    }
}
