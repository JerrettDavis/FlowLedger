using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class RecurringFlowConfiguration : IEntityTypeConfiguration<RecurringFlow>
{
    public void Configure(EntityTypeBuilder<RecurringFlow> builder)
    {
        builder.ToTable("recurring_flows");

        builder.HasKey(rf => rf.Id);
        builder.Property(rf => rf.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_id");

        builder.Ignore(rf => rf.RecurringFlowId);

        builder.Property<TenantId>("TenantId")
            .HasColumnName("tenant_id")
            .HasConversion(StronglyTypedIdConverters.TenantIdConverter)
            .IsRequired();

        builder.Property<AccountId>("AccountId")
            .HasColumnName("account_id")
            .HasConversion(StronglyTypedIdConverters.AccountIdConverter)
            .IsRequired();

        builder.Property(rf => rf.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        // Money: Amount
        builder.OwnsOne(rf => rf.Amount, money =>
        {
            money.Property(m => m.Amount)
                .HasColumnName("amount")
                .HasColumnType("numeric(18,4)")
                .IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .HasConversion(CurrencyConverter.Instance)
                .IsRequired();
        });

        builder.Property(rf => rf.AmountModel)
            .HasColumnName("amount_model")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(rf => rf.Direction)
            .HasColumnName("direction")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // RecurrencePattern — owned type with several nullable columns.
        builder.OwnsOne(rf => rf.Pattern, pattern =>
        {
            pattern.Property(p => p.Frequency)
                .HasColumnName("recurrence_frequency")
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();

            pattern.Property(p => p.DayOfMonth)
                .HasColumnName("recurrence_day_of_month");

            pattern.Property(p => p.SecondDayOfMonth)
                .HasColumnName("recurrence_second_day_of_month");

            pattern.Property(p => p.IntervalWeeks)
                .HasColumnName("recurrence_interval_weeks");

            pattern.Property(p => p.AnchorDayOfWeek)
                .HasColumnName("recurrence_anchor_day_of_week")
                .HasConversion<string>()
                .HasMaxLength(15);
        });

        // DateOnlyRange — owned type (start required, end optional).
        builder.OwnsOne(rf => rf.ActiveWindow, window =>
        {
            window.Property(w => w.Start)
                .HasColumnName("active_start")
                .IsRequired();

            window.Property(w => w.End)
                .HasColumnName("active_end");

            window.Ignore(w => w.IsOpenEnded);
        });

        builder.Property<CategoryId?>("CategoryId")
            .HasColumnName("category_id")
            .HasConversion(StronglyTypedIdConverters.NullableCategoryIdConverter);

        builder.Property(rf => rf.Counterparty)
            .HasColumnName("counterparty")
            .HasMaxLength(300);

        builder.Property(rf => rf.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(rf => rf.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Owned collection: PlannedFlowOccurrence
        // The public Occurrences property (IReadOnlyList) wraps the private _occurrences backing field.
        // EF convention auto-discovers _occurrences via the camelCase naming rule, so no explicit
        // HasField config is needed for the navigation itself.
        builder.OwnsMany(rf => rf.Occurrences, occ =>
        {
            occ.ToTable("planned_flow_occurrences");
            occ.WithOwner().HasForeignKey("recurring_flow_id");

            occ.HasKey(o => o.Id);
            occ.Property(o => o.Id)
                .HasColumnName("id")
                .ValueGeneratedNever()
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_id");

            // PlannedOccurrenceId computed from _id backing field — ignore typed property.
            occ.Ignore(o => o.PlannedOccurrenceId);

            // RecurringFlowId is derived from the FK column managed by WithOwner — just ignore.
            occ.Ignore(o => o.RecurringFlowId);

            occ.Property<Guid>("_tenantId")
                .HasColumnName("tenant_id")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .IsRequired();
            occ.Ignore(o => o.TenantId);

            occ.Property<Guid>("_accountId")
                .HasColumnName("account_id")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .IsRequired();
            occ.Ignore(o => o.AccountId);

            occ.OwnsOne(o => o.PlannedAmount, money =>
            {
                money.Property(m => m.Amount)
                    .HasColumnName("planned_amount")
                    .HasColumnType("numeric(18,4)")
                    .IsRequired();

                money.Property(m => m.Currency)
                    .HasColumnName("planned_currency")
                    .HasMaxLength(3)
                    .HasConversion(CurrencyConverter.Instance)
                    .IsRequired();
            });

            // Ensure EF navigates PlannedAmount via property accessor (not a backing field).
            occ.Navigation(o => o.PlannedAmount).UsePropertyAccessMode(PropertyAccessMode.Property);

            occ.Property(o => o.Direction)
                .HasColumnName("direction")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            occ.Property(o => o.PlannedDate)
                .HasColumnName("planned_date")
                .IsRequired();

            occ.Property(o => o.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            // MatchedTransactionId — backed by _matchedTransactionId (Guid?) field.
            occ.Property<Guid?>("_matchedTransactionId")
                .HasColumnName("matched_transaction_id")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            occ.Ignore(o => o.MatchedTransactionId);

            // AmountVariance — nullable Money owned type.
            occ.OwnsOne(o => o.AmountVariance, money =>
            {
                money.Property(m => m.Amount)
                    .HasColumnName("amount_variance")
                    .HasColumnType("numeric(18,4)");

                money.Property(m => m.Currency)
                    .HasColumnName("amount_variance_currency")
                    .HasMaxLength(3)
                    .HasConversion(CurrencyConverter.Instance);
            });

            occ.Property(o => o.DateVarianceDays)
                .HasColumnName("date_variance_days");

            // ConfidenceScore — backed by _matchConfidence (decimal?) field, stored as decimal.
            occ.Property<decimal?>("_matchConfidence")
                .HasColumnName("match_confidence")
                .HasColumnType("numeric(5,4)")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            occ.Ignore(o => o.MatchConfidence);

            occ.HasIndex(nameof(PlannedFlowOccurrence.PlannedDate))
                .HasDatabaseName("ix_planned_occurrences_date");
        });

        // Indexes
        builder.HasIndex("TenantId").HasDatabaseName("ix_recurring_flows_tenant_id");
        builder.HasIndex("TenantId", "AccountId")
            .HasDatabaseName("ix_recurring_flows_tenant_account");
    }
}
