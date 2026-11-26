using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class HomeAssistantSettingsRecordConfiguration : IEntityTypeConfiguration<HomeAssistantSettingsRecord>
{
    public void Configure(EntityTypeBuilder<HomeAssistantSettingsRecord> builder)
    {
        builder.ToTable("home_assistant_settings");
        builder.HasKey(r => r.TenantId);
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Enabled).HasColumnName("enabled").HasDefaultValue(false);
        builder.Property(r => r.Webhook).HasColumnName("webhook");
        builder.Property(r => r.ShareAmounts).HasColumnName("share_amounts").HasDefaultValue(false);
        builder.Property(r => r.IncludeNames).HasColumnName("include_names").HasDefaultValue(true);
        builder.Property(r => r.LeadDays).HasColumnName("lead_days").HasDefaultValue(3);
        builder.Property(r => r.Kinds).HasColumnName("kinds").HasDefaultValue(string.Empty);
        builder.Property(r => r.LastSentUtc).HasColumnName("last_sent_utc");
        builder.Property(r => r.LastError).HasColumnName("last_error");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}

internal sealed class HomeAssistantSentRecordConfiguration : IEntityTypeConfiguration<HomeAssistantSentRecord>
{
    public void Configure(EntityTypeBuilder<HomeAssistantSentRecord> builder)
    {
        builder.ToTable("home_assistant_sent");
        builder.HasKey(r => new { r.TenantId, r.DedupeKey });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.DedupeKey).HasColumnName("dedupe_key");
        builder.Property(r => r.SentUtc).HasColumnName("sent_utc").HasDefaultValueSql("now()");
    }
}
