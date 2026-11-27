using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AppUserLinkCodeRecordConfiguration : IEntityTypeConfiguration<AppUserLinkCodeRecord>
{
    public void Configure(EntityTypeBuilder<AppUserLinkCodeRecord> builder)
    {
        builder.ToTable("app_user_link_codes");
        builder.HasKey(r => r.CodeHash);
        builder.Property(r => r.CodeHash).HasColumnName("code_hash");
        builder.Property(r => r.TargetTenantId).HasColumnName("tenant_id");
        builder.Property(r => r.CreatedUtc).HasColumnName("created_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.ExpiresUtc).HasColumnName("expires_utc");
        builder.Property(r => r.UsedUtc).HasColumnName("used_utc");
    }
}
