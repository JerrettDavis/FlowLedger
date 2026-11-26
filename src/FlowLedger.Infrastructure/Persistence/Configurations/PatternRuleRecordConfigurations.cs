using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class MerchantAliasRecordConfiguration : IEntityTypeConfiguration<MerchantAliasRecord>
{
    public void Configure(EntityTypeBuilder<MerchantAliasRecord> builder)
    {
        builder.ToTable("merchant_aliases");
        builder.HasKey(r => new { r.TenantId, r.Pattern });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Pattern).HasColumnName("pattern");
        builder.Property(r => r.Alias).HasColumnName("alias");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}

internal sealed class CategoryRuleRecordConfiguration : IEntityTypeConfiguration<CategoryRuleRecord>
{
    public void Configure(EntityTypeBuilder<CategoryRuleRecord> builder)
    {
        builder.ToTable("category_rules");
        builder.HasKey(r => new { r.TenantId, r.Pattern });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Pattern).HasColumnName("pattern");
        builder.Property(r => r.CategoryPath).HasColumnName("category_path");
        builder.Property(r => r.UpdatedUtc).HasColumnName("updated_utc").HasDefaultValueSql("now()");
    }
}
