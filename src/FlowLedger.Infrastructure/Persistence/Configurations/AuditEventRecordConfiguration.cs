using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventRecordConfiguration : IEntityTypeConfiguration<AuditEventRecord>
{
    public void Configure(EntityTypeBuilder<AuditEventRecord> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.OccurredUtc).HasColumnName("occurred_utc");
        builder.Property(r => r.ActingTenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Actor).HasColumnName("actor");
        builder.Property(r => r.Action).HasColumnName("action");
        builder.Property(r => r.Target).HasColumnName("target");
        builder.Property(r => r.RemoteIp).HasColumnName("remote_ip");
        builder.Property(r => r.TraceId).HasColumnName("trace_id");
        builder.Property(r => r.StatusCode).HasColumnName("status_code");
        builder.HasIndex(r => r.OccurredUtc).HasDatabaseName("ix_audit_events_time");
        builder.HasIndex(r => new { r.ActingTenantId, r.OccurredUtc }).HasDatabaseName("ix_audit_events_tenant_time");
    }
}
