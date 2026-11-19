namespace FlowLedger.Application.Features.HomeAssistant;

/// <summary>Event kinds FlowLedger can send to Home Assistant. The wire value is the snake_case string.</summary>
public static class HaEventKinds
{
    public const string BillDueSoon = "bill_due_soon";
    public const string BillPaid = "bill_paid";
    public const string PaycheckExpected = "paycheck_expected";
    public const string PaycheckReceived = "paycheck_received";
    public const string Overdraft = "overdraft";
    public const string LowBalanceForecast = "low_balance_forecast";
    public const string BudgetAlert = "budget_alert";

    public static IReadOnlyList<string> All { get; } =
        [BillDueSoon, BillPaid, PaycheckExpected, PaycheckReceived, Overdraft, LowBalanceForecast, BudgetAlert];
}

/// <summary>
/// Per-household Home Assistant sharing settings. The webhook URL is a secret and is never part of this read model:
/// <see cref="HasWebhook"/> says whether one is stored and <see cref="WebhookHost"/> shows only its host.
/// </summary>
public sealed record HomeAssistantSettings(
    bool Enabled,
    bool HasWebhook,
    string? WebhookHost,
    bool ShareAmounts,
    bool IncludeNames,
    int LeadDays,
    IReadOnlyList<string> Kinds,
    DateTimeOffset? LastSentUtc,
    string? LastError)
{
    public const int DefaultLeadDays = 3;

    public static HomeAssistantSettings Default { get; } =
        new(false, false, null, false, true, DefaultLeadDays, HaEventKinds.All, null, null);
}

/// <summary>An event ready to send. Amount/Balance are only ever populated when the household opted in to sharing numbers.</summary>
public sealed record HaEvent(
    string Kind,
    string Severity,
    string Title,
    string Message,
    string DedupeKey,
    string? Subject = null,
    DateOnly? Date = null,
    int? DueInDays = null,
    decimal? Amount = null,
    decimal? Balance = null);

/// <summary>Inputs to the pure detector (no I/O).</summary>
public sealed record HaFlow(Guid Id, string Name, string? Counterparty, bool IsIncome, decimal Amount, IReadOnlyList<DateOnly> Dates);

public sealed record HaAccountBalance(Guid Id, string Name, decimal Balance, bool IsLiquid);

public sealed record HaRecentTransaction(string Description, string? Merchant, bool IsIncome, decimal Amount, DateOnly Date);

public sealed record HaBudgetBreach(string Category, bool Over);
