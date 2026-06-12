using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class SyncCursorRecordConfiguration : IEntityTypeConfiguration<SyncCursorRecord>
{
    public void Configure(EntityTypeBuilder<SyncCursorRecord> builder)
    {
        builder.ToTable("sync_cursors");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(r => r.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(r => r.ProviderName)
            .HasColumnName("provider_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(r => r.ProviderAccountId)
            .HasColumnName("provider_account_id")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(r => r.CursorValue)
            .HasColumnName("cursor_value")
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(r => r.LastSyncedAt)
            .HasColumnName("last_synced_at")
            .IsRequired();

        builder.Property(r => r.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Unique composite key for the business identity
        builder.HasIndex(r => new { r.TenantId, r.ProviderName, r.ProviderAccountId })
            .IsUnique()
            .HasDatabaseName("uq_sync_cursors_tenant_provider_account");

        // Fast lookup index per tenant
        builder.HasIndex(r => r.TenantId)
            .HasDatabaseName("ix_sync_cursors_tenant_id");
    }
}
