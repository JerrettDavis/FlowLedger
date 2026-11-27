using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AppUserRecordConfiguration : IEntityTypeConfiguration<AppUserRecord>
{
    public void Configure(EntityTypeBuilder<AppUserRecord> builder)
    {
        builder.ToTable("app_users");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.Issuer).HasColumnName("issuer");
        builder.Property(r => r.Subject).HasColumnName("subject");
        builder.Property(r => r.Email).HasColumnName("email");
        builder.Property(r => r.DisplayName).HasColumnName("display_name");
        builder.Property(r => r.HomeTenantId).HasColumnName("tenant_id");
        builder.Property(r => r.CreatedUtc).HasColumnName("created_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.LastLoginUtc).HasColumnName("last_login_utc").HasDefaultValueSql("now()");
        builder.Property(r => r.EmailVerified).HasColumnName("email_verified").HasDefaultValue(false);

        // One login always lands in the same tenant. The name is the one Postgres gave the original inline UNIQUE constraint, so
        // databases created by the old store already have it. (A lower(email) index also exists; EF cannot model expressions, so the
        // migration creates ix_app_users_email by SQL.)
        builder.HasIndex(r => new { r.Issuer, r.Subject }).IsUnique().HasDatabaseName("app_users_issuer_subject_key");
        builder.HasIndex(r => r.HomeTenantId).HasDatabaseName("ix_app_users_tenant");
    }
}
