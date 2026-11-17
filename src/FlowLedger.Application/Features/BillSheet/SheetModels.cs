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
    bool IsActive,
    string Frequency = BillFrequency.Monthly,
    int? IntervalWeeks = null,
    DateOnly? AnchorDate = null,
    int? SecondDueDay = null);

/// <summary>How often a sheet row is paid. <see cref="Estimated"/> amounts are per payment, not per month.</summary>
public static class BillFrequency
{
    public const string Monthly = "Monthly";
    public const string TwiceMonthly = "TwiceMonthly";
    public const string Weekly = "Weekly";
    public const string EveryNWeeks = "EveryNWeeks";

    public static bool IsKnown(string? f) => f is Monthly or TwiceMonthly or Weekly or EveryNWeeks;
}

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
    SheetPaymentSource Source,
    int Slot = 0);

/// <summary>Minimal transaction projection the matcher works on.</summary>
/// <param name="Text">Merchant plus description, used for matching.</param>
/// <param name="Label">Display name of the merchant, used when detecting recurring expenses.</param>
public sealed record SheetTxn(Guid Id, DateOnly Date, decimal Amount, string Text, string? Label = null);

/// <summary>Result of a matcher run: cells to write and cells to clear.</summary>
public sealed record SheetMatchResult(
    IReadOnlyList<SheetPaymentData> Upserts,
    IReadOnlyList<(Guid BillId, DateOnly Month, int Slot)> Clears);
