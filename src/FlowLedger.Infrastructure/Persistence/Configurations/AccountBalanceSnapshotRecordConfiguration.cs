using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AccountBalanceSnapshotRecordConfiguration : IEntityTypeConfiguration<AccountBalanceSnapshotRecord>
{
    public void Configure(EntityTypeBuilder<AccountBalanceSnapshotRecord> builder)
    {
        builder.ToTable("account_balance_snapshots");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(r => r.ProviderName).HasColumnName("provider_name").HasMaxLength(100).IsRequired();
        builder.Property(r => r.ProviderAccountId).HasColumnName("provider_account_id").HasMaxLength(200).IsRequired();
        builder.Property(r => r.Balance).HasColumnName("balance").HasPrecision(19, 4).IsRequired();
        builder.Property(r => r.Currency).HasColumnName("currency").HasMaxLength(8).IsRequired();
        builder.Property(r => r.CapturedAt).HasColumnName("captured_at").IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.AccountId, r.CapturedAt })
            .HasDatabaseName("ix_balance_snapshots_tenant_account_captured");
    }
}
