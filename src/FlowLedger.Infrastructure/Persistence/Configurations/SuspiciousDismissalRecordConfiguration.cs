using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class SuspiciousDismissalRecordConfiguration : IEntityTypeConfiguration<SuspiciousDismissalRecord>
{
    public void Configure(EntityTypeBuilder<SuspiciousDismissalRecord> builder)
    {
        builder.ToTable("suspicious_dismissals");
        builder.HasKey(r => new { r.TenantId, r.TransactionId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.Note).HasColumnName("note");
        builder.Property(r => r.DismissedUtc).HasColumnName("dismissed_utc").HasDefaultValueSql("now()");
    }
}
