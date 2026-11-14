using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowLedger.Infrastructure.Persistence.Configurations;

internal sealed class SheetBillRecordConfiguration : IEntityTypeConfiguration<SheetBillRecord>
{
    public void Configure(EntityTypeBuilder<SheetBillRecord> builder)
    {
        builder.ToTable("sheet_bills");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.Section).HasColumnName("section").HasMaxLength(100).IsRequired();
        builder.Property(r => r.SortOrder).HasColumnName("sort_order").IsRequired();
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(r => r.Payer).HasColumnName("payer").HasMaxLength(100);
        builder.Property(r => r.IsDebt).HasColumnName("is_debt");
        builder.Property(r => r.IsPerpetual).HasColumnName("is_perpetual");
        builder.Property(r => r.IsOptional).HasColumnName("is_optional");
        builder.Property(r => r.IsBusiness).HasColumnName("is_business");
        builder.Property(r => r.AutoPay).HasColumnName("auto_pay");
        builder.Property(r => r.DueDay).HasColumnName("due_day");
        builder.Property(r => r.Estimated).HasColumnName("estimated").HasColumnType("numeric(18,4)");
        builder.Property(r => r.Actual).HasColumnName("actual").HasColumnType("numeric(18,4)");
        builder.Property(r => r.Service).HasColumnName("service").HasMaxLength(300);
        builder.Property(r => r.AccountRef).HasColumnName("account_ref").HasMaxLength(300);
        builder.Property(r => r.MatchText).HasColumnName("match_text").HasMaxLength(300);
        builder.Property(r => r.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(r => r.TenantId).HasDatabaseName("ix_sheet_bills_tenant_id");
        // Concurrent refreshes (page load, sync, prerender) must not create the same detected row twice.
        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique()
            .HasFilter("section = 'Detected'").HasDatabaseName("ux_sheet_bills_detected_name");
    }
}

internal sealed class SheetPaymentRecordConfiguration : IEntityTypeConfiguration<SheetPaymentRecord>
{
    public void Configure(EntityTypeBuilder<SheetPaymentRecord> builder)
    {
        builder.ToTable("sheet_payments");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.BillId).HasColumnName("bill_id").IsRequired();
        builder.Property(r => r.Month).HasColumnName("month").IsRequired();
        builder.Property(r => r.IsPaid).HasColumnName("is_paid");
        builder.Property(r => r.Amount).HasColumnName("amount").HasColumnType("numeric(18,4)");
        builder.Property(r => r.PaidOn).HasColumnName("paid_on");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.Source).HasColumnName("source").HasMaxLength(10).IsRequired();

        builder.HasOne<SheetBillRecord>().WithMany().HasForeignKey(r => r.BillId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.BillId, r.Month }).IsUnique().HasDatabaseName("ux_sheet_payments_bill_month");
        // A transaction may back at most one cell, even with concurrent refreshes.
        builder.HasIndex(r => new { r.TenantId, r.TransactionId }).IsUnique()
            .HasFilter("transaction_id IS NOT NULL").HasDatabaseName("ux_sheet_payments_transaction");
        builder.HasIndex(r => r.TenantId).HasDatabaseName("ix_sheet_payments_tenant_id");
    }
}
