using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AccountConnectorLinkRecordConfiguration : IEntityTypeConfiguration<AccountConnectorLinkRecord>
{
    public void Configure(EntityTypeBuilder<AccountConnectorLinkRecord> builder)
    {
        builder.ToTable("account_connector_links");
        builder.HasKey(r => new { r.TenantId, r.AccountId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.AccountId).HasColumnName("account_id");
        builder.Property(r => r.ConnectorId).HasColumnName("connector_id").HasConversion(ConnectorIdConverter.Instance);
        builder.Property(r => r.ProviderAccountId).HasColumnName("provider_account_id");
        builder.Property(r => r.LinkedUtc).HasColumnName("linked_utc").HasDefaultValueSql("now()");
        builder.HasIndex(r => new { r.TenantId, r.ConnectorId, r.ProviderAccountId })
            .IsUnique()
            .HasDatabaseName("ux_account_connector_links_provider");
    }
}
