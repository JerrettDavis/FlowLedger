using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AccountRateRecordConfiguration : IEntityTypeConfiguration<AccountRateRecord>
{
    public void Configure(EntityTypeBuilder<AccountRateRecord> builder)
    {
        builder.ToTable("account_rates");
        builder.HasKey(r => new { r.TenantId, r.AccountId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.AccountId).HasColumnName("account_id");
        builder.Property(r => r.RatePercent).HasColumnName("rate_percent").HasPrecision(7, 3);
        builder.Property(r => r.RemainingMonths).HasColumnName("remaining_months");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}
