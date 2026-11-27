namespace FlowLedger.Application.Features.Strategies;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SuggestionKind { Cash, Savings, Budget, Debt, Investing, Housing }

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SuggestionSeverity { Info, Opportunity, Attention, Urgent }

/// <summary>One actionable, explainable recommendation. Everything is derived from the user's data plus labelled assumptions.</summary>
public sealed record Suggestion(
    string Id,
    SuggestionKind Kind,
    SuggestionSeverity Severity,
    string Title,
    string Summary,
    decimal? AnnualImpact,
    double Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Assumptions,
    string? Simulator = null);

internal sealed record AccountSnapshot(
    Guid Id, string Name, string Type, decimal Balance, bool IsInvestment, bool IsLiability,
    decimal? Rate = null, int? RemainingMonths = null);

internal sealed record MonthFlow(string Month, decimal Income, decimal Spend);

internal sealed record StrategyInputs(
    IReadOnlyList<AccountSnapshot> Accounts,
    IReadOnlyList<MonthFlow> Months,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> CategoryByMonth,
    MarketRates Rates,
    DateOnly Today);

public sealed record StrategyReport(
    DateTimeOffset GeneratedAt,
    MarketRates Rates,
    decimal LiquidCash,
    decimal AvgMonthlySpend,
    decimal AvgMonthlyIncome,
    double? SavingsRate,
    double? EmergencyMonths,
    IReadOnlyList<Suggestion> Suggestions,
    string Disclaimer,
    CoachPlan? Coach = null,
    CashTrajectory? Trajectory = null);
