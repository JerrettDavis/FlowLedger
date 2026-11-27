using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class ProviderApiCallRecordConfiguration : IEntityTypeConfiguration<ProviderApiCallRecord>
{
    public void Configure(EntityTypeBuilder<ProviderApiCallRecord> builder)
    {
        builder.ToTable("provider_api_calls");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Provider).HasColumnName("provider");
        builder.Property(r => r.Method).HasColumnName("method");
        builder.Property(r => r.Path).HasColumnName("path");
        builder.Property(r => r.StatusCode).HasColumnName("status_code");
        builder.Property(r => r.DurationMs).HasColumnName("duration_ms");
        builder.Property(r => r.ResponseBytes).HasColumnName("response_bytes");
        builder.Property(r => r.OccurredUtc).HasColumnName("occurred_utc");
        builder.Property(r => r.Error).HasColumnName("error");
        builder.HasIndex(r => new { r.Provider, r.OccurredUtc }).HasDatabaseName("ix_provider_api_calls_provider_time");
    }
}
