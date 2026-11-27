using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class WebhookReplayRecordConfiguration : IEntityTypeConfiguration<WebhookReplayRecord>
{
    public void Configure(EntityTypeBuilder<WebhookReplayRecord> builder)
    {
        builder.ToTable("webhook_replays");
        builder.HasKey(r => new { r.Provider, r.BodyHash });
        builder.Property(r => r.Provider).HasColumnName("provider");
        builder.Property(r => r.BodyHash).HasColumnName("body_hash");
        builder.Property(r => r.SeenUtc).HasColumnName("seen_utc").HasDefaultValueSql("now()");
        builder.HasIndex(r => r.SeenUtc).HasDatabaseName("ix_webhook_replays_seen");
    }
}
