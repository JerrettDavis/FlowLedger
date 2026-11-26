using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageRecordConfiguration : IEntityTypeConfiguration<OutboxMessageRecord>
{
    public void Configure(EntityTypeBuilder<OutboxMessageRecord> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.EventType).HasColumnName("event_type");
        builder.Property(r => r.Payload).HasColumnName("payload");
        builder.Property(r => r.OccurredUtc).HasColumnName("occurred_utc");
        builder.Property(r => r.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(r => r.NextAttemptUtc).HasColumnName("next_attempt_utc");
        builder.Property(r => r.LockedUntilUtc).HasColumnName("locked_until_utc");
        builder.Property(r => r.LockToken).HasColumnName("lock_token");
        builder.Property(r => r.CompletedHandlers).HasColumnName("completed_handlers").HasDefaultValue(string.Empty);
        builder.Property(r => r.ProcessedUtc).HasColumnName("processed_utc");
        builder.Property(r => r.DeadLetteredUtc).HasColumnName("dead_lettered_utc");
        builder.Property(r => r.LastError).HasColumnName("last_error");
        builder.HasIndex(r => new { r.TenantId, r.NextAttemptUtc })
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_utc IS NULL AND dead_lettered_utc IS NULL");
    }
}
