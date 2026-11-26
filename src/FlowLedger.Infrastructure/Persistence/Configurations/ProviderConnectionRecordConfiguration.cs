using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class ProviderConnectionRecordConfiguration : IEntityTypeConfiguration<ProviderConnectionRecord>
{
    public void Configure(EntityTypeBuilder<ProviderConnectionRecord> builder)
    {
        builder.ToTable("provider_connections");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.ProviderName).HasColumnName("provider_name").HasMaxLength(100).IsRequired();
        builder.Property(r => r.MemberProviderId).HasColumnName("member_provider_id").HasMaxLength(400).IsRequired();
        builder.Property(r => r.Institution).HasColumnName("institution").HasMaxLength(200).IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasMaxLength(50).IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Property(r => r.IssueKind).HasColumnName("issue_kind").HasMaxLength(50);
        builder.Property(r => r.IssueMessage).HasColumnName("issue_message").HasMaxLength(500);
        builder.Property(r => r.IssueRetryable).HasColumnName("issue_retryable").HasDefaultValue(false);
        builder.Property(r => r.IssueRequiresUserAction).HasColumnName("issue_requires_user_action").HasDefaultValue(false);
        builder.Property(r => r.IssueRetryAfter).HasColumnName("issue_retry_after");
        builder.Property(r => r.IssueAt).HasColumnName("issue_at");
        builder.Property(r => r.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);

        // One connection per (tenant, provider) — the DB itself refuses a second provision.
        builder.HasIndex(r => new { r.TenantId, r.ProviderName })
            .IsUnique()
            .HasDatabaseName("uq_provider_connections_tenant_provider");
    }
}
