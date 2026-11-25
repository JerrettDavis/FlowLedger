namespace FlowLedger.Application.Features.Strategies;

/// <summary>
/// Market benchmark rates (annual %, e.g. 4.25 = 4.25%). Deposit rates are estimates derived from Treasury yields
/// (retail best-available products track them closely); loan rates come from Federal Reserve / Freddie Mac series.
/// <see cref="Live"/> names the inputs fetched live; everything else is a labelled assumption.
/// </summary>
public sealed record MarketRates(
    DateOnly AsOf,
    decimal Hysa,
    decimal MoneyMarket,
    decimal Cd6Month,
    decimal Cd1Year,
    decimal Cd2Year,
    decimal Cd5Year,
    decimal Mortgage30,
    decimal Mortgage15,
    decimal Auto48Month,
    decimal PersonalLoan,
    decimal CreditCard,
    decimal TypicalCheckingApy,
    decimal TypicalSavingsApy,
    IReadOnlyList<string> Live,
    string Source)
{
    /// <summary>Conservative fallbacks used when live rates are unavailable. Clearly labelled as assumptions in the UI.</summary>
    public static MarketRates Defaults(DateOnly asOf) => new(
        asOf, Hysa: 4.00m, MoneyMarket: 3.90m, Cd6Month: 4.00m, Cd1Year: 4.00m, Cd2Year: 3.80m, Cd5Year: 3.80m,
        Mortgage30: 6.50m, Mortgage15: 5.75m, Auto48Month: 8.00m, PersonalLoan: 12.00m, CreditCard: 21.00m,
        TypicalCheckingApy: 0.07m, TypicalSavingsApy: 0.45m, Live: [], Source: "assumed defaults (live rates unavailable)");
}

/// <summary>Supplies current market benchmark rates. Implementations must never receive user data.</summary>
public interface IMarketRateProvider
{
    Task<MarketRates> GetAsync(CancellationToken ct = default);
}

/// <summary>Always returns the static defaults (tests, offline installs).</summary>
public sealed class StaticMarketRateProvider(MarketRates? rates = null) : IMarketRateProvider
{
    public Task<MarketRates> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(rates ?? MarketRates.Defaults(DateOnly.FromDateTime(DateTime.UtcNow)));
}
