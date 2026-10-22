using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stanza.ControlPlane.Automation;

public sealed class AutomationQueueGuard
{
    public int Id { get; set; }
    public long Revision { get; set; }
}

public sealed class ActionDefinition
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string Concurrency { get; set; } = "queue";
    public long Revision { get; set; }
    public Guid? ActiveRunId { get; set; }
    [JsonIgnore] public string ProtectedSteps { get; set; } = "";
}

public sealed class AutomationSchedule
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid? ActionId { get; set; }
    public string Operation { get; set; } = "deploy";
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "0 * * * *";
    public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; }
    public string Overlap { get; set; } = "skip";
    public string MissedRun { get; set; } = "skip";
    public long NextRunAt { get; set; }
    public long Revision { get; set; }
}

public sealed class WebhookDefinition
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid? ActionId { get; set; }
    public string Name { get; set; } = "";
    public string Operation { get; set; } = "deploy";
    public string Provider { get; set; } = "github";
    public string Events { get; set; } = "push";
    public string Refs { get; set; } = "";
    public string ParameterMapping { get; set; } = "{}";
    public bool Enabled { get; set; }
    public long Revision { get; set; }
    public long RateWindow { get; set; }
    public int RateCount { get; set; }
    [JsonIgnore] public string ProtectedSecret { get; set; } = "";
}

public sealed class ImageUpdatePolicy
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DeploymentId { get; set; }
    public string Name { get; set; } = "";
    public string Services { get; set; } = "";
    public string Mode { get; set; } = "notify-only";
    public bool Enabled { get; set; }
    public int FrequencyMinutes { get; set; } = 60;
    public int FailureBackoffMinutes { get; set; } = 60;
    public long NextRunAt { get; set; }
    public long? LastCheckAt { get; set; }
    public string LastResult { get; set; } = "not-checked";
    public string DigestsJson { get; set; } = "[]";
    public long Revision { get; set; }
    public Guid? ActiveRunId { get; set; }
}

public sealed class AutomationRun
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? ActionId { get; set; }
    public Guid? ActionOwnerId { get; set; }
    public Guid DefinitionId { get; set; }
    public string Source { get; set; } = "manual";
    public string OccurrenceKey { get; set; } = "";
    public string Operation { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Code { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public long LeaseUntil { get; set; }
    public Guid? LeaseOwner { get; set; }
    public bool CancelRequested { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? ReconciledBy { get; set; }
    public long? ReconciledAt { get; set; }
    [JsonIgnore] public string ProtectedPlan { get; set; } = "";
    [JsonIgnore] public string ProtectedParameters { get; set; } = "";
}

public sealed class AutomationStepRun
{
    public Guid RunId { get; set; }
    public int Index { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? AgentId { get; set; }
    public string Kind { get; set; } = "";
    public Guid? OperationId { get; set; }
    public string State { get; set; } = "pending";
    public string Code { get; set; } = "";
}

public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid WebhookId { get; set; }
    public string DeliveryId { get; set; } = "";
    public string Event { get; set; } = "";
    public long ReceivedAt { get; set; }
    public string State { get; set; } = "";
    public Guid? RunId { get; set; }
}

public sealed class ActionDefinitionConfiguration : IEntityTypeConfiguration<ActionDefinition>
{
    public void Configure(EntityTypeBuilder<ActionDefinition> b)
    {
        b.ToTable("AutomationActions").HasKey(x => x.Id); b.Property(x => x.Revision).IsConcurrencyToken();
        b.Property(x => x.Name).HasMaxLength(100); b.HasIndex(x => x.OwnerId);
    }
}
public sealed class AutomationQueueGuardConfiguration : IEntityTypeConfiguration<AutomationQueueGuard>
{
    public void Configure(EntityTypeBuilder<AutomationQueueGuard> b)
    {
        b.ToTable("AutomationQueueGuard").HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasData(new AutomationQueueGuard { Id = 1, Revision = 0 });
    }
}
public sealed class ScheduleConfiguration : IEntityTypeConfiguration<AutomationSchedule>
{
    public void Configure(EntityTypeBuilder<AutomationSchedule> b)
    {
        b.ToTable("AutomationSchedules").HasKey(x => x.Id); b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.Enabled, x.NextRunAt });
    }
}
public sealed class WebhookConfiguration : IEntityTypeConfiguration<WebhookDefinition>
{
    public void Configure(EntityTypeBuilder<WebhookDefinition> b)
    {
        b.ToTable("AutomationWebhooks").HasKey(x => x.Id); b.Property(x => x.Revision).IsConcurrencyToken();
    }
}
public sealed class ImagePolicyConfiguration : IEntityTypeConfiguration<ImageUpdatePolicy>
{
    public void Configure(EntityTypeBuilder<ImageUpdatePolicy> b)
    {
        b.ToTable("AutomationImagePolicies").HasKey(x => x.Id); b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.Enabled, x.NextRunAt });
    }
}
public sealed class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    public void Configure(EntityTypeBuilder<AutomationRun> b)
    {
        b.ToTable("AutomationRuns").HasKey(x => x.Id); b.HasIndex(x => x.OccurrenceKey).IsUnique();
        b.Property(x => x.OccurrenceKey).HasMaxLength(200); b.HasIndex(x => new { x.State, x.CreatedAt });
        b.HasIndex(x => new { x.DefinitionId, x.State });
    }
}
public sealed class AutomationStepConfiguration : IEntityTypeConfiguration<AutomationStepRun>
{
    public void Configure(EntityTypeBuilder<AutomationStepRun> b)
    {
        b.ToTable("AutomationSteps").HasKey(x => new { x.RunId, x.Index });
        b.HasOne<AutomationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("AutomationWebhookDeliveries").HasKey(x => x.Id);
        b.HasIndex(x => new { x.WebhookId, x.DeliveryId }).IsUnique();
        b.Property(x => x.DeliveryId).HasMaxLength(200); b.Property(x => x.Event).HasMaxLength(100);
    }
}
