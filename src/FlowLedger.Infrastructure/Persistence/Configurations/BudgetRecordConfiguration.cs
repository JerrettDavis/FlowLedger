using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class BudgetRecordConfiguration : IEntityTypeConfiguration<BudgetRecord>
{
    public void Configure(EntityTypeBuilder<BudgetRecord> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(r => new { r.TenantId, r.Category });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Category).HasColumnName("category");
        builder.Property(r => r.MonthlyLimit).HasColumnName("monthly_limit").HasPrecision(19, 4);
        builder.Property(r => r.Rollover).HasColumnName("rollover").HasDefaultValue(false);
        builder.Property(r => r.CarriedAmount).HasColumnName("carried_amount").HasPrecision(19, 4).HasDefaultValue(0m);
        builder.Property(r => r.CarriedForMonth).HasColumnName("carried_for_month").HasMaxLength(7);
        builder.Property(r => r.RowVersion).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}
