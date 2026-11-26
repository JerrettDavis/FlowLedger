using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class GoalRecordConfiguration : IEntityTypeConfiguration<GoalRecord>
{
    public void Configure(EntityTypeBuilder<GoalRecord> builder)
    {
        builder.ToTable("savings_goals");
        builder.HasKey(r => new { r.TenantId, r.Id });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name");
        builder.Property(r => r.TargetAmount).HasColumnName("target_amount").HasPrecision(18, 2);
        builder.Property(r => r.SavedAmount).HasColumnName("saved_amount").HasPrecision(18, 2);
        builder.Property(r => r.TargetDate).HasColumnName("target_date").HasColumnType("date");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}
