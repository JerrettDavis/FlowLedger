namespace FlowLedger.Contracts;

/// <summary>Net worth components for one currency.</summary>
public sealed record CurrencyTotalDto(
    string Currency,
    decimal AccountAssets,
    decimal AccountLiabilities,
    decimal PhysicalAssets,
    decimal NetWorth,
    decimal CarriedByAccounts);
