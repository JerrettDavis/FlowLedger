using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Investments;

/// <summary>
/// Investments are assets (they count toward net worth), not bills or income. Their contributions, trades and dividends
/// must stay out of recurring flows and the bill sheet.
/// </summary>
public static class InvestmentExclusion
{
    /// <summary>True for the top-level "Investments" / "Investment" category and anything beneath it.</summary>
    public static bool IsInvestmentCategory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var top = path.Split('/', 2)[0].Trim();
        return top.Equals("Investments", StringComparison.OrdinalIgnoreCase)
            || top.Equals("Investment", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accounts that hold investments: the Investment type, or any account tracked as a holding.</summary>
    public static bool IsInvestmentAccount(Account a) =>
        a.AccountType == AccountType.Investment || a.Mode == AccountMode.Holding;
}
