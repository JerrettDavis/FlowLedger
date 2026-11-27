using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class BackupRunRecordConfiguration : IEntityTypeConfiguration<BackupRunRecord>
{
    public void Configure(EntityTypeBuilder<BackupRunRecord> builder)
    {
        builder.ToTable("backup_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.StartedUtc).HasColumnName("started_utc");
        builder.Property(r => r.FinishedUtc).HasColumnName("finished_utc");
        builder.Property(r => r.Ok).HasColumnName("ok");
        builder.Property(r => r.SnapshotDir).HasColumnName("snapshot_dir");
        builder.Property(r => r.Rows).HasColumnName("rows");
        builder.Property(r => r.OffsiteOk).HasColumnName("offsite_ok");
        builder.Property(r => r.OffsiteDetail).HasColumnName("offsite_detail");
        builder.Property(r => r.DrillOk).HasColumnName("drill_ok");
        builder.Property(r => r.Error).HasColumnName("error");
        builder.Property(r => r.DrillDetail).HasColumnName("drill_detail").HasColumnType("jsonb");
    }
}
