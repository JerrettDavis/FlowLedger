namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>One row of the bill sheet (tenant-scoped).</summary>
internal sealed class SheetBillRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Section { get; set; } = "Bills";
    public int SortOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Payer { get; set; }
    public bool IsDebt { get; set; }
    public bool IsPerpetual { get; set; }
    public bool IsOptional { get; set; }
    public bool IsBusiness { get; set; }
    public bool AutoPay { get; set; }
    public int? DueDay { get; set; }
    public decimal Estimated { get; set; }
    public decimal? Actual { get; set; }
    public string? Service { get; set; }
    public string? AccountRef { get; set; }
    public string? MatchText { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One cell (bill x month). Unique per bill+month; a transaction can back at most one cell.</summary>
internal sealed class SheetPaymentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BillId { get; set; }
    public DateOnly Month { get; set; }
    public bool IsPaid { get; set; }
    public decimal? Amount { get; set; }
    public DateOnly? PaidOn { get; set; }
    public Guid? TransactionId { get; set; }
    public string Source { get; set; } = "Manual";
}
