using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class IntegrationRecordConfiguration : IEntityTypeConfiguration<IntegrationRecord>
{
    public void Configure(EntityTypeBuilder<IntegrationRecord> builder)
    {
        builder.ToTable("integrations");
        builder.HasKey(r => new { r.TenantId, r.Id });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name");
        builder.Property(r => r.Kind).HasColumnName("kind");
        builder.Property(r => r.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(r => r.Config).HasColumnName("config");
        builder.Property(r => r.Secrets).HasColumnName("secrets");
        builder.Property(r => r.CreatedUtc).HasColumnName("created_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.LastUsedUtc).HasColumnName("last_used_utc");
        builder.Property(r => r.LastError).HasColumnName("last_error");
    }
}

internal sealed class IntegrationDeliveryRecordConfiguration : IEntityTypeConfiguration<IntegrationDeliveryRecord>
{
    public void Configure(EntityTypeBuilder<IntegrationDeliveryRecord> builder)
    {
        builder.ToTable("integration_deliveries");
        builder.HasKey(r => new { r.TenantId, r.Id });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.IntegrationId).HasColumnName("integration_id");
        builder.Property(r => r.AtUtc).HasColumnName("at_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.Trigger).HasColumnName("trigger");
        builder.Property(r => r.Method).HasColumnName("method");
        builder.Property(r => r.Target).HasColumnName("target");
        builder.Property(r => r.StatusCode).HasColumnName("status_code");
        builder.Property(r => r.DurationMs).HasColumnName("duration_ms");
        builder.Property(r => r.Error).HasColumnName("error");
        builder.HasIndex(r => new { r.TenantId, r.IntegrationId, r.AtUtc })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_integration_deliveries_integration");
    }
}
