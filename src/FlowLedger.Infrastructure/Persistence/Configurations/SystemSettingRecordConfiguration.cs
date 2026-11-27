using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class SystemSettingRecordConfiguration : IEntityTypeConfiguration<SystemSettingRecord>
{
    public void Configure(EntityTypeBuilder<SystemSettingRecord> builder)
    {
        builder.ToTable("system_settings");
        builder.HasKey(r => r.Key);
        builder.Property(r => r.Key).HasColumnName("key");
        builder.Property(r => r.Value).HasColumnName("value");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}
