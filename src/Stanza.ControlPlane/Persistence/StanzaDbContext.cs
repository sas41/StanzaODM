using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Persistence;

public sealed class AccountEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    public AccountRole Role { get; set; }
    public bool Enabled { get; set; }
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GroupEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class GroupMembershipEntity
{
    public Guid AccountId { get; set; }
    public Guid GroupId { get; set; }
}

public sealed class GrantEntity
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? GroupId { get; set; }
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public Guid? ResourceId { get; set; }
}

public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class SecurityGuard
{
    public int Id { get; set; }
    public long Revision { get; set; }
}

public sealed class StanzaDbContext(DbContextOptions<StanzaDbContext> options) : DbContext(options)
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<GroupEntity> Groups => Set<GroupEntity>();
    public DbSet<GroupMembershipEntity> GroupMemberships => Set<GroupMembershipEntity>();
    public DbSet<GrantEntity> Grants => Set<GrantEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var accounts = modelBuilder.Entity<AccountEntity>();
        accounts.ToTable("SecurityAccounts");
        accounts.HasKey(x => x.Id);
        accounts.HasIndex(x => x.NormalizedUsername).IsUnique();
        accounts.Property(x => x.Username).HasMaxLength(100);
        accounts.Property(x => x.NormalizedUsername).HasMaxLength(100);
        accounts.Property(x => x.PasswordHash).HasMaxLength(1024);
        accounts.Property(x => x.SecurityStamp).HasMaxLength(64);
        modelBuilder.Entity<GroupEntity>().ToTable("SecurityGroups").HasKey(x => x.Id);
        modelBuilder.Entity<GroupEntity>().Property(x => x.Name).HasMaxLength(100);
        modelBuilder.Entity<GroupEntity>().HasIndex(x => x.Name).IsUnique();
        var memberships = modelBuilder.Entity<GroupMembershipEntity>();
        memberships.ToTable("SecurityGroupMemberships").HasKey(x => new { x.AccountId, x.GroupId });
        memberships.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        memberships.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        var grants = modelBuilder.Entity<GrantEntity>();
        grants.ToTable("SecurityGrants", table => table.HasCheckConstraint("CK_Grant_OneOwner",
            "(\"AccountId\" IS NOT NULL AND \"GroupId\" IS NULL) OR (\"AccountId\" IS NULL AND \"GroupId\" IS NOT NULL)"));
        grants.HasKey(x => x.Id);
        grants.Property(x => x.Action).HasMaxLength(100);
        grants.Property(x => x.ResourceType).HasMaxLength(100);
        grants.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        grants.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SettingEntity>().ToTable("Settings").HasKey(x => x.Key);
        modelBuilder.Entity<SettingEntity>().Property(x => x.Key).HasMaxLength(200);
        modelBuilder.Entity<SecurityGuard>().ToTable("SecurityGuard").HasKey(x => x.Id);
        modelBuilder.Entity<SecurityGuard>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StanzaDbContext).Assembly);
    }
}
