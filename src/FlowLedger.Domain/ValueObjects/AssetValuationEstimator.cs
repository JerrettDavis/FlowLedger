using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Pure projection of an asset's value to a given date using its valuation model, anchored on the
/// latest recorded valuation. Never returns a negative value and never projects backwards.
/// </summary>
public static class AssetValuationEstimator
{
    private const decimal DaysPerYear = 365.25m;

    public static Money Estimate(Asset asset, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var anchor = asset.CurrentValue;
        var days = asOf.DayNumber - asset.ValuedOn.DayNumber;
        if (days <= 0 || asset.Method == ValuationMethod.Manual)
        {
            return anchor;
        }

        var years = days / DaysPerYear;
        var amount = asset.Method switch
        {
            ValuationMethod.DecliningBalance => Compound(anchor.Amount, 1m - asset.AnnualRate, years),
            ValuationMethod.Appreciation => Compound(anchor.Amount, 1m + asset.AnnualRate, years),
            ValuationMethod.StraightLine => StraightLine(asset, anchor.Amount, years),
            _ => anchor.Amount,
        };

        return new Money(Math.Max(0m, Math.Round(amount, 2, MidpointRounding.AwayFromZero)), anchor.Currency);
    }

    private static decimal StraightLine(Asset asset, decimal anchor, decimal years)
    {
        if (asset.UsefulLifeYears is null or <= 0)
        {
            return anchor;
        }

        var salvage = asset.SalvageValue ?? 0m;
        var basis = asset.PurchasePrice?.Amount ?? anchor;
        var annualDrop = Math.Max(0m, basis - salvage) / asset.UsefulLifeYears!.Value;
        return Math.Max(Math.Min(salvage, anchor), anchor - annualDrop * years);
    }

    // Fractional-year compounding has no decimal implementation; confine the double math here and
    // round straight back so no floating-point value is ever stored or compared.
    private static decimal Compound(decimal anchor, decimal factor, decimal years)
    {
        if (factor <= 0m)
        {
            return 0m;
        }

        var growth = Math.Pow((double)factor, (double)years);
        return anchor * (decimal)growth;
    }
}
