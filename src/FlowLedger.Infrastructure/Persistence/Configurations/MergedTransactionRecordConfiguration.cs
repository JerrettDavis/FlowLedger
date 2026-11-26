using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class MergedTransactionRecordConfiguration : IEntityTypeConfiguration<MergedTransactionRecord>
{
    public void Configure(EntityTypeBuilder<MergedTransactionRecord> builder)
    {
        builder.ToTable("merged_transactions");
        builder.HasKey(r => new { r.TenantId, r.DuplicateId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.DuplicateId).HasColumnName("duplicate_id");
        builder.Property(r => r.SurvivorId).HasColumnName("survivor_id");
        builder.Property(r => r.Fingerprint).HasColumnName("fingerprint");
        builder.Property(r => r.MergedUtc).HasColumnName("merged_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.Auto).HasColumnName("auto").HasDefaultValue(false);
        builder.Property(r => r.Reason).HasColumnName("reason").HasDefaultValue(string.Empty);
        builder.Property(r => r.Snapshot).HasColumnName("snapshot").HasDefaultValue(string.Empty);
        builder.HasIndex(r => new { r.TenantId, r.Fingerprint }).HasDatabaseName("ix_merged_transactions_tenant_fingerprint");
    }
}

internal sealed class DuplicateDismissalRecordConfiguration : IEntityTypeConfiguration<DuplicateDismissalRecord>
{
    public void Configure(EntityTypeBuilder<DuplicateDismissalRecord> builder)
    {
        builder.ToTable("duplicate_dismissals");
        builder.HasKey(r => new { r.TenantId, r.PairKey });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.PairKey).HasColumnName("pair_key");
        builder.Property(r => r.DismissedUtc).HasColumnName("dismissed_utc").HasDefaultValueSql("now()");
    }
}
