using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");

        // The aggregate exposes Id (Guid) backed by the _id Guid field.
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_id");

        // AccountId is computed from the same backing Guid — ignore to avoid double mapping.
        builder.Ignore(a => a.AccountId);

        // TenantId stored as Guid column via value converter.
        builder.Property<TenantId>("TenantId")
            .HasColumnName("tenant_id")
            .HasConversion(StronglyTypedIdConverters.TenantIdConverter)
            .IsRequired();

        builder.Property(a => a.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.AccountType)
            .HasColumnName("account_type")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Money: CurrentBalance — two scalar columns. NEVER float.
        // Currency is stored as a plain string via value converter (not a nested owned type).
        builder.OwnsOne(a => a.CurrentBalance, money =>
        {
            money.Property(m => m.Amount)
                .HasColumnName("balance_amount")
                .HasColumnType("numeric(18,4)")
                .IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("balance_currency")
                .HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance)
                .IsRequired();
        });

        // Money: CreditLimit — nullable. EF OwnsOne with all-nullable columns.
        builder.OwnsOne(a => a.CreditLimit, money =>
        {
            money.Property(m => m.Amount)
                .HasColumnName("credit_limit_amount")
                .HasColumnType("numeric(18,4)");

            money.Property(m => m.Currency)
                .HasColumnName("credit_limit_currency")
                .HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance);
        });

        builder.Property(a => a.Institution)
            .HasColumnName("institution")
            .HasMaxLength(200);

        builder.Property(a => a.ExternalAccountRef)
            .HasColumnName("external_account_ref")
            .HasMaxLength(500);

        builder.Property(a => a.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(a => a.LastBalanceConfirmedAt)
            .HasColumnName("last_balance_confirmed_at");

        builder.Property(a => a.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        // Indexes per PLAN §23
        builder.HasIndex("TenantId").HasDatabaseName("ix_accounts_tenant_id");
    }
}
