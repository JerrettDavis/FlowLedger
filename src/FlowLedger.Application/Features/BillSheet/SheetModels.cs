namespace FlowLedger.Application.Features.BillSheet;

/// <summary>Stored definition of one row of the bill sheet.</summary>
public sealed record SheetBillData(
    Guid Id,
    string Section,
    int SortOrder,
    string Name,
    string? Payer,
    bool IsDebt,
    bool IsPerpetual,
    bool IsOptional,
    bool IsBusiness,
    bool AutoPay,
    int? DueDay,
    decimal Estimated,
    decimal? Actual,
    string? Service,
    string? AccountRef,
    string? MatchText,
    bool IsActive);

public enum SheetPaymentSource
{
    /// <summary>Set by the matcher from a real transaction.</summary>
    Auto,

    /// <summary>Set by the user; never touched by automation.</summary>
    Manual,
}

/// <summary>One cell of the grid (bill x month). A manual unpaid cell suppresses automation.</summary>
public sealed record SheetPaymentData(
    Guid BillId,
    DateOnly Month,
    bool IsPaid,
    decimal? Amount,
    DateOnly? PaidOn,
    Guid? TransactionId,
    SheetPaymentSource Source);

/// <summary>Minimal transaction projection the matcher works on.</summary>
public sealed record SheetTxn(Guid Id, DateOnly Date, decimal Amount, string Text);

/// <summary>Result of a matcher run: cells to write and cells to clear.</summary>
public sealed record SheetMatchResult(
    IReadOnlyList<SheetPaymentData> Upserts,
    IReadOnlyList<(Guid BillId, DateOnly Month)> Clears);
