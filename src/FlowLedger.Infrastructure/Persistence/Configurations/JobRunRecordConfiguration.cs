using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class JobRunRecordConfiguration : IEntityTypeConfiguration<JobRunRecord>
{
    public void Configure(EntityTypeBuilder<JobRunRecord> builder)
    {
        builder.ToTable("job_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Job).HasColumnName("job");
        builder.Property(r => r.StartedUtc).HasColumnName("started_utc");
        builder.Property(r => r.FinishedUtc).HasColumnName("finished_utc");
        builder.Property(r => r.Success).HasColumnName("success");
        builder.Property(r => r.Error).HasColumnName("error");
        builder.Property(r => r.Attempt).HasColumnName("attempt").HasDefaultValue(1);
        builder.HasIndex(r => r.FinishedUtc).HasDatabaseName("ix_job_runs_finished");
    }
}
