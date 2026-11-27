using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_id");

        builder.Ignore(t => t.TransactionId);

        builder.Property<TenantId>("TenantId")
            .HasColumnName("tenant_id")
            .HasConversion(StronglyTypedIdConverters.TenantIdConverter)
            .IsRequired();

        builder.Property<AccountId>("AccountId")
            .HasColumnName("account_id")
            .HasConversion(StronglyTypedIdConverters.AccountIdConverter)
            .IsRequired();

        // Money: Amount — numeric(19,4), never float.
        builder.OwnsOne(t => t.Amount, money =>
        {
            money.Property(m => m.Amount)
                .HasColumnName("amount")
                .HasColumnType("numeric(19,4)")
                .IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance)
                .IsRequired();
        });

        builder.Property(t => t.Direction)
            .HasColumnName("direction")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasColumnName("description")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(t => t.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(t => t.EffectiveDate)
            .HasColumnName("effective_date")
            .IsRequired();

        builder.Property(t => t.PostedDate)
            .HasColumnName("posted_date");

        builder.Property<CategoryId?>("CategoryId")
            .HasColumnName("category_id")
            .HasConversion(StronglyTypedIdConverters.NullableCategoryIdConverter);

        // Existing rows default to Manual: their category (if any) came from a user or an import, never from the auto-categorizer.
        builder.Property(t => t.CategorySource)
            .HasColumnName("category_source")
            .HasConversion<string>()
            .HasMaxLength(10)
            .HasDefaultValue(CategorySource.Manual)
            .IsRequired();

        builder.Property(t => t.CategoryConfidence).HasColumnName("category_confidence");

        builder.Property<CategoryId?>("SuggestedCategoryId")
            .HasColumnName("suggested_category_id")
            .HasConversion(StronglyTypedIdConverters.NullableCategoryIdConverter);

        builder.Property(t => t.SuggestedConfidence).HasColumnName("suggested_confidence");

        builder.Property(t => t.ProviderCategory)
            .HasColumnName("provider_category")
            .HasMaxLength(Transaction.MaxProviderCategoryLength);

        builder.Property(t => t.TransferLinkId).HasColumnName("transfer_link_id");

        builder.Property(t => t.MerchantName)
            .HasColumnName("merchant_name")
            .HasMaxLength(300);

        builder.Property(t => t.Notes)
            .HasColumnName("notes")
            .HasMaxLength(2000);

        builder.Property(t => t.SuggestedName)
            .HasColumnName("suggested_name")
            .HasMaxLength(300);

        // Comma-joined; the public Tags list is derived from this backing field.
        builder.Ignore(t => t.Tags);
        builder.Property<string?>("_tags")
            .HasColumnName("tags")
            .HasMaxLength(500);

        // TransactionFingerprint — owned type (single string column).
        builder.OwnsOne(t => t.Fingerprint, fp =>
        {
            fp.Property(f => f.Value)
                .HasColumnName("fingerprint")
                .HasMaxLength(512);
        });

        builder.Property<PlannedOccurrenceId?>("MatchedOccurrenceId")
            .HasColumnName("matched_occurrence_id")
            .HasConversion(StronglyTypedIdConverters.NullablePlannedOccurrenceIdConverter);

        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Splits — owned collection of TransactionSplit value items.
        builder.OwnsMany(t => t.Splits, split =>
        {
            split.ToTable("transaction_splits");
            split.WithOwner().HasForeignKey("transaction_id");
            split.Property<int>("Id")
                .HasColumnName("id")
                .ValueGeneratedOnAdd();
            split.HasKey("Id");

            split.OwnsOne(s => s.Amount, money =>
            {
                money.Property(m => m.Amount)
                    .HasColumnName("amount")
                    .HasColumnType("numeric(19,4)")
                    .IsRequired();

                money.Property(m => m.Currency)
                    .HasColumnName("currency")
                    .HasMaxLength(3)
                    .HasConversion(CurrencyConverter.Instance)
                    .IsRequired();
            });

            split.Property<CategoryId?>("CategoryId")
                .HasColumnName("category_id")
                .HasConversion(StronglyTypedIdConverters.NullableCategoryIdConverter);

            split.Property(s => s.Notes)
                .HasColumnName("notes")
                .HasMaxLength(2000);
        });

        // Optimistic concurrency (#157): Postgres' own xmin system column, so no schema change is needed.
        builder.Property(t => t.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Indexes per PLAN §23: tenant + account + date composite for fast cashflow queries.
        builder.HasIndex("TenantId", "AccountId", nameof(Transaction.EffectiveDate))
            .HasDatabaseName("ix_transactions_tenant_account_date");

        builder.HasIndex("TenantId", nameof(Transaction.EffectiveDate))
            .HasDatabaseName("ix_transactions_tenant_date");

        // Matches the list ordering (EffectiveDate DESC, CreatedAt DESC) so paged lists need no extra sort.
        builder.HasIndex("TenantId", nameof(Transaction.EffectiveDate), nameof(Transaction.CreatedAt))
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_transactions_tenant_date_created");

        // Fingerprint uniqueness index — added as raw SQL in the migration because
        // HasIndex on owned-type columns requires the exact internal shadow property name.
        // See InitialCreate migration for the actual index DDL:
        //   CREATE UNIQUE INDEX uq_transactions_tenant_fingerprint
        //     ON transactions (tenant_id, fingerprint)
        //     WHERE fingerprint IS NOT NULL;
    }
}
