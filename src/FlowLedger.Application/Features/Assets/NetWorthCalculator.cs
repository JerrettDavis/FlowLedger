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
/// Amounts are summed as-is (single-currency assumption, same as the existing dashboard).
/// </summary>
public static class NetWorthCalculator
{
    private static readonly HashSet<AccountType> AssetAccountTypes =
        [AccountType.Checking, AccountType.Savings, AccountType.Investment, AccountType.Cash, AccountType.ManualAsset];

    private static readonly HashSet<AccountType> LiabilityAccountTypes =
        [AccountType.CreditCard, AccountType.Loan, AccountType.Mortgage, AccountType.ManualLiability];

    public static bool IsLiability(AccountType type) => LiabilityAccountTypes.Contains(type);

    public static NetWorthDto Compute(IReadOnlyList<Account> accounts, IReadOnlyList<Asset> assets, DateOnly asOf)
    {
        var activeAccounts = accounts.Where(a => a.IsActive).ToDictionary(a => a.Id);

        var accountAssets = activeAccounts.Values
            .Where(a => AssetAccountTypes.Contains(a.AccountType)).Sum(a => a.CurrentBalance.Amount);
        var liabilities = activeAccounts.Values
            .Where(a => LiabilityAccountTypes.Contains(a.AccountType)).Sum(a => Math.Abs(a.CurrentBalance.Amount));

        decimal physical = 0m;
        decimal carried = 0m;
        foreach (var asset in assets.Where(a => a.IsActive))
        {
            var value = AssetValuationEstimator.Estimate(asset, asOf).Amount;
            if (asset.LinkedAccountId is { } linkId
                && activeAccounts.TryGetValue(linkId, out var linked)
                && linked.AccountType == AccountType.ManualAsset)
            {
                carried += value;
                continue;
            }

            physical += value;
        }

        return new NetWorthDto(accountAssets, liabilities, physical, accountAssets - liabilities + physical, carried);
    }
}
