using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class RecurringDismissalRecordConfiguration : IEntityTypeConfiguration<RecurringDismissalRecord>
{
    public void Configure(EntityTypeBuilder<RecurringDismissalRecord> builder)
    {
        builder.ToTable("recurring_dismissals");
        builder.HasKey(r => new { r.TenantId, r.SuggestionKey });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.SuggestionKey).HasColumnName("suggestion_key");
        builder.Property(r => r.DismissedUtc).HasColumnName("dismissed_utc").HasDefaultValueSql("now()");
    }
}
