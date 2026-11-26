using FlowLedger.Domain.ValueObjects;
namespace FlowLedger.Application.Features.Strategies;

public sealed record FireResult(
    decimal FireNumber, decimal CurrentInvested, decimal MonthlyContribution, int? MonthsToFire, decimal? FireAge,
    decimal ProjectedAtHorizon, decimal ProjectedAnnualIncome, bool Reachable, IReadOnlyList<GrowthPoint> Path);

/// <summary>Financial-independence math: how long until invested assets can fund spending at a safe withdrawal rate.</summary>
public static class FirePlanner
{
    public const int MaxYears = 60;

    /// <param name="annualSpend">Yearly spending to be funded in retirement.</param>
    /// <param name="withdrawalRatePercent">Safe withdrawal rate, e.g. 4.</param>
    /// <param name="realReturnPercent">Expected inflation-adjusted annual return.</param>
    public static FireResult Plan(
        decimal invested, decimal monthlyContribution, decimal annualSpend, decimal withdrawalRatePercent,
        decimal realReturnPercent, decimal? currentAge, int horizonYears)
    {
        if (withdrawalRatePercent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(withdrawalRatePercent));
        }

        horizonYears = Math.Clamp(horizonYears, 1, MaxYears);
        var fireNumber = MoneyRounding.Round(annualSpend / (withdrawalRatePercent / 100m), 2);
        var path = Simulators.Grow(invested, monthlyContribution, realReturnPercent, horizonYears * 12);

        int? months = null;
        foreach (var p in path)
        {
            if (p.Balance >= fireNumber)
            {
                months = p.Month;
                break;
            }
        }

        var end = path[^1].Balance;
        return new FireResult(
            fireNumber, invested, monthlyContribution, months,
            months is { } m && currentAge is { } age ? MoneyRounding.Round(age + m / 12m, 1) : null,
            end, MoneyRounding.Round(end * withdrawalRatePercent / 100m, 2), months is not null, path);
    }
}
