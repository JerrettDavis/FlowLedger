using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class JobFireMarkRecordConfiguration : IEntityTypeConfiguration<JobFireMarkRecord>
{
    public void Configure(EntityTypeBuilder<JobFireMarkRecord> builder)
    {
        builder.ToTable("job_fire_marks");
        builder.HasKey(r => r.Job);
        builder.Property(r => r.Job).HasColumnName("job").HasMaxLength(200);
        builder.Property(r => r.LastFireUtc).HasColumnName("last_fire_utc");
        builder.Property(r => r.Owner).HasColumnName("owner").HasMaxLength(100);
        builder.Property(r => r.ClaimedUtc).HasColumnName("claimed_utc");
        builder.Property(r => r.CompletedUtc).HasColumnName("completed_utc");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc");
    }
}
