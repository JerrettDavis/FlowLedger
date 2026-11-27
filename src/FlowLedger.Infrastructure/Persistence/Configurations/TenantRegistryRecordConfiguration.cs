using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class TenantRegistryRecordConfiguration : IEntityTypeConfiguration<TenantRegistryRecord>
{
    public void Configure(EntityTypeBuilder<TenantRegistryRecord> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.Status).HasColumnName("status").HasDefaultValue("active");
        builder.Property(r => r.CreatedUtc).HasColumnName("created_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.OrphanedUtc).HasColumnName("orphaned_utc");
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");
    }
}
