using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class ProviderAccountRecordConfiguration : IEntityTypeConfiguration<ProviderAccountRecord>
{
    public void Configure(EntityTypeBuilder<ProviderAccountRecord> builder)
    {
        builder.ToTable("connector_provider_accounts");
        builder.HasKey(r => new { r.TenantId, r.ConnectorId, r.ProviderAccountId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.ConnectorId).HasColumnName("connector_id").HasConversion(ConnectorIdConverter.Instance);
        builder.Property(r => r.ProviderAccountId).HasColumnName("provider_account_id");
        builder.Property(r => r.Name).HasColumnName("name");
        builder.Property(r => r.AccountType).HasColumnName("account_type");
        builder.Property(r => r.Balance).HasColumnName("balance").HasPrecision(19, 4);
        builder.Property(r => r.Currency).HasColumnName("currency");
        builder.Property(r => r.LastSeenUtc).HasColumnName("last_seen_utc").HasDefaultValueSql("now()");
    }
}
