using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Assets;

/// <summary>
/// The one place net worth is computed. Pure and deterministic so the double-counting rules can be
/// unit tested:
///  • account assets (checking, savings, investment, cash, manual-asset) minus account liabilities;
///  • plus each active physical asset at its estimated value, EXCEPT assets linked to an active
///    ManualAsset account — that account's balance already carries the value (recording a valuation
///    pushes it there), so counting both would double count;
///  • an asset linked to a Loan/Mortgage/CreditCard/ManualLiability account is counted at full value;
///    the liability is already subtracted via accounts, so equity (value − liability) is informational;
///  • a link to a deactivated or unknown account is ignored (the asset counts as unlinked).
/// Currencies are never added together: totals are computed per currency. With a single currency the top-level figures are
/// that currency's totals; with several, <see cref="NetWorthDto.MixedCurrencies"/> is set, <see cref="NetWorthDto.ByCurrency"/>
/// lists every currency, and the top-level figures describe only the primary (most populated) currency.
/// </summary>
internal static class NetWorthCalculator
{
    private static readonly HashSet<AccountType> AssetAccountTypes =
        [AccountType.Checking, AccountType.Savings, AccountType.Investment, AccountType.Cash, AccountType.ManualAsset];

    private static readonly HashSet<AccountType> LiabilityAccountTypes =
        [AccountType.CreditCard, AccountType.Loan, AccountType.Mortgage, AccountType.ManualLiability];

    public static bool IsLiability(AccountType type) => LiabilityAccountTypes.Contains(type);

    public static NetWorthDto Compute(IReadOnlyList<Account> accounts, IReadOnlyList<Asset> assets, DateOnly asOf)
    {
        var activeAccounts = accounts.Where(a => a.IsActive).ToDictionary(a => a.Id);

        var accountAssets = new Dictionary<string, decimal>();
        var liabilities = new Dictionary<string, decimal>();
        var physical = new Dictionary<string, decimal>();
        var carried = new Dictionary<string, decimal>();
        var population = new Dictionary<string, int>();

        static void Add(Dictionary<string, decimal> d, string c, decimal v) => d[c] = d.GetValueOrDefault(c) + v;

        foreach (var a in activeAccounts.Values)
        {
            var c = a.CurrentBalance.Currency.Code;
            population[c] = population.GetValueOrDefault(c) + 1;
            if (AssetAccountTypes.Contains(a.AccountType))
            {
                Add(accountAssets, c, a.CurrentBalance.Amount);
            }
            else if (LiabilityAccountTypes.Contains(a.AccountType))
            {
                Add(liabilities, c, a.CurrentBalance.Amount); // sign preserved (#125): an overpaid card reduces liabilities
            }
        }

        foreach (var asset in assets.Where(a => a.IsActive))
        {
            var estimate = AssetValuationEstimator.Estimate(asset, asOf);
            var c = estimate.Currency.Code;
            population[c] = population.GetValueOrDefault(c) + 1;
            if (asset.LinkedAccountId is { } linkId
                && activeAccounts.TryGetValue(linkId, out var linked)
                && linked.AccountType == AccountType.ManualAsset)
            {
                Add(carried, c, estimate.Amount);
                continue;
            }

            Add(physical, c, estimate.Amount);
        }

        var totals = population.Keys.OrderBy(c => c, StringComparer.Ordinal).Select(c =>
        {
            var aa = accountAssets.GetValueOrDefault(c);
            var li = liabilities.GetValueOrDefault(c);
            var ph = physical.GetValueOrDefault(c);
            return new CurrencyTotalDto(c, aa, li, ph, aa - li + ph, carried.GetValueOrDefault(c));
        }).ToList();

        if (totals.Count == 0)
        {
            return new NetWorthDto(0m, 0m, 0m, 0m, 0m);
        }

        var primaryCode = population.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        var primary = totals.Single(t => t.Currency == primaryCode);
        return new NetWorthDto(
            primary.AccountAssets, primary.AccountLiabilities, primary.PhysicalAssets, primary.NetWorth, primary.CarriedByAccounts,
            primary.Currency, totals.Count > 1, totals);
    }
}
