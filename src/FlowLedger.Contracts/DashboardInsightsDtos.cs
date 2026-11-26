namespace FlowLedger.Contracts;

public sealed record CategorySpendDto(string Category, decimal Amount);

public sealed record MonthCashFlowDto(string Month, decimal Income, decimal Spending);

public sealed record NetWorthPointDto(string Date, decimal Value);

public sealed record MerchantSpendDto(string Merchant, decimal Amount, int Visits);

/// <summary>A category whose spending changed most against the previous period of the same length.</summary>
public sealed record CategoryMoverDto(string Category, decimal Current, decimal Previous, decimal Change);

public sealed record DashboardInsightsDto(
    List<CategorySpendDto> SpendingByCategory,
    List<MonthCashFlowDto> CashFlow,
    List<NetWorthPointDto> NetWorth,
    List<MerchantSpendDto>? TopMerchants = null,
    List<CategoryMoverDto>? Movers = null,
    string? PreviousLabel = null);
