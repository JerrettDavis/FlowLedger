using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class SavedReportRecordConfiguration : IEntityTypeConfiguration<SavedReportRecord>
{
    public void Configure(EntityTypeBuilder<SavedReportRecord> builder)
    {
        builder.ToTable("saved_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(10).IsRequired();
        builder.Property(r => r.PanelsJson).HasColumnName("panels_json").IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(r => r.TenantId).HasDatabaseName("ix_saved_reports_tenant_id");
    }
}
