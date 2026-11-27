using FlowLedger.Domain.ValueObjects;
namespace FlowLedger.Application.Features.Strategies;

public sealed record GrowthPoint(int Month, decimal Balance, decimal Contributed, decimal Interest);

public sealed record GrowthComparison(
    IReadOnlyList<GrowthPoint> Baseline, IReadOnlyList<GrowthPoint> Scenario, decimal Difference);

public sealed record PastPoint(string Month, decimal ExtraWealth);

public sealed record PastWhatIfResult(IReadOnlyList<PastPoint> Points, decimal TotalExtra, decimal FromSaving, decimal FromYield);

public sealed record RefinanceResult(
    decimal CurrentPayment, decimal NewPayment, decimal MonthlySavings, decimal ClosingCosts,
    int? BreakEvenMonths, decimal CurrentRemainingInterest, decimal NewTotalInterest, decimal LifetimeSavings, bool Worthwhile);

public sealed record LadderRung(string Label, int Months, decimal Amount, decimal Apy, decimal InterestAtMaturity);

public sealed record LadderPlan(
    IReadOnlyList<LadderRung> Rungs, decimal BlendedApy, decimal AnnualInterest, decimal SavingsAnnualInterest, decimal Advantage);

/// <summary>Standard amortised-loan payment. Zero-rate loans amortise linearly.</summary>
internal static class LoanMath
{
    public static decimal Payment(decimal balance, decimal annualRatePercent, int months)
    {
        if (months <= 0 || balance <= 0)
        {
            return 0m;
        }

        var r = annualRatePercent / 1200m;
        if (r == 0)
        {
            return MoneyRounding.Round(balance / months, 2);
        }

        // payment = balance * r / (1 - (1 + r)^-n), in decimal so cents are exact (no binary-double drift) and a long term or a
        // high rate shrinks toward 0 instead of overflowing.
        var discount = PowInt(1m / (1m + r), months);
        return MoneyRounding.Round(balance * r / (1m - discount), 2);
    }

    /// <summary><paramref name="b"/> to a non-negative integer power by repeated squaring, entirely in decimal.</summary>
    public static decimal PowInt(decimal b, int n)
    {
        var result = 1m;
        while (n > 0)
        {
            if ((n & 1) == 1)
            {
                result *= b;
            }

            n >>= 1;
            if (n > 0)
            {
                b *= b;
            }
        }

        return result;
    }
}

public static class Simulators
{
    /// <summary>Longest simulated horizon: 60 years, matching <see cref="FirePlanner.MaxYears"/>.</summary>
    public const int MaxMonths = 720;

    /// <summary>
    /// Monthly rate equivalent to an annual rate quoted as an effective yield (APY): (1 + apy)^(1/12) - 1, so twelve
    /// compounding months land exactly on the quoted APY rather than flattering it.
    /// </summary>
    public static decimal MonthlyRate(decimal apyPercent)
        => (decimal)(Math.Pow(1.0 + (double)apyPercent / 100.0, 1.0 / 12.0) - 1.0);

    /// <summary>Monthly-compounded growth of a starting balance plus recurring contributions.</summary>
    public static IReadOnlyList<GrowthPoint> Grow(decimal start, decimal monthlyContribution, decimal annualRatePercent, int months)
    {
        months = Math.Clamp(months, 0, MaxMonths);
        var points = new List<GrowthPoint>(months + 1) { new(0, MoneyRounding.Round(start, 2), MoneyRounding.Round(start, 2), 0m) };
        var balance = start;
        var contributed = start;
        var rate = MonthlyRate(annualRatePercent);
        for (var m = 1; m <= months; m++)
        {
            balance += balance * rate;
            balance += monthlyContribution;
            contributed += monthlyContribution;
            points.Add(new GrowthPoint(m, MoneyRounding.Round(balance, 2), MoneyRounding.Round(contributed, 2), MoneyRounding.Round(balance - contributed, 2)));
        }

        return points;
    }

    public static GrowthComparison CompareGrowth(
        decimal start, decimal baseContribution, decimal baseRate, decimal scenarioContribution, decimal scenarioRate, int months)
    {
        var a = Grow(start, baseContribution, baseRate, months);
        var b = Grow(start, scenarioContribution, scenarioRate, months);
        return new GrowthComparison(a, b, b[^1].Balance - a[^1].Balance);
    }

    /// <summary>
    /// "What if I'd done this in the past?" Replays history month by month: <paramref name="extraByMonth"/> is money you
    /// would have saved each month, which compounds at <paramref name="growthRatePercent"/>; <paramref name="idleCash"/> would have
    /// earned the spread between <paramref name="toRate"/> and <paramref name="fromRate"/> the whole time.
    /// </summary>
    public static PastWhatIfResult PastWhatIf(
        IReadOnlyList<(string Month, decimal Extra)> extraByMonth, decimal growthRatePercent,
        decimal idleCash, decimal fromRate, decimal toRate)
    {
        var points = new List<PastPoint>();
        decimal wealth = 0, saved = 0;
        var growth = MonthlyRate(growthRatePercent);
        var spread = idleCash * (toRate - fromRate) / 1200m;
        foreach (var (month, extra) in extraByMonth)
        {
            wealth += wealth * growth;
            wealth += extra + spread;
            saved += extra;
            points.Add(new PastPoint(month, MoneyRounding.Round(wealth, 2)));
        }

        var total = MoneyRounding.Round(wealth, 2);
        return new PastWhatIfResult(points, total, MoneyRounding.Round(saved, 2), MoneyRounding.Round(total - saved, 2));
    }

    /// <summary>Refinance break-even: new payment vs current payment over the remaining term, including closing costs.</summary>
    public static RefinanceResult Refinance(
        decimal balance, decimal currentRate, int remainingMonths, decimal newRate, int newTermMonths, decimal closingCosts)
    {
        var curPay = LoanMath.Payment(balance, currentRate, remainingMonths);
        var newBalance = balance + closingCosts; // roll costs in; the conservative case
        var newPay = LoanMath.Payment(newBalance, newRate, newTermMonths);
        var monthlySavings = curPay - newPay;
        var curInterest = Math.Max(0, curPay * remainingMonths - balance);
        var newInterest = Math.Max(0, newPay * newTermMonths - newBalance);
        // Lifetime view charges the new loan's interest; closing costs are in the new principal, so they are already counted.
        var lifetime = curInterest - newInterest;
        int? breakEven = monthlySavings > 0 && closingCosts > 0 ? (int)Math.Ceiling(closingCosts / monthlySavings) : closingCosts <= 0 && monthlySavings > 0 ? 0 : null;
        var worthwhile = lifetime > 0 && monthlySavings > 0 && (breakEven is null || breakEven <= Math.Min(remainingMonths, newTermMonths));
        return new RefinanceResult(curPay, newPay, MoneyRounding.Round(monthlySavings, 2), closingCosts, breakEven,
            MoneyRounding.Round(curInterest, 2), MoneyRounding.Round(newInterest, 2), MoneyRounding.Round(lifetime, 2), worthwhile);
    }
}

public static class LadderPlanner
{
    /// <summary>Equal rungs at 6 months, 1, 2 and 5 years (a classic ladder). Compares against the same money in high-yield savings.</summary>
    public static LadderPlan Build(decimal amount, MarketRates r)
    {
        (string Label, int Months, decimal Apy)[] spec =
        [
            ("6-month", 6, r.Cd6Month), ("1-year", 12, r.Cd1Year), ("2-year", 24, r.Cd2Year), ("5-year", 60, r.Cd5Year),
        ];
        var each = Math.Floor(amount / spec.Length / 100m) * 100m;
        var rungs = spec.Select(s => new LadderRung(
            s.Label, s.Months, each, s.Apy, MoneyRounding.Round(each * s.Apy / 100m * s.Months / 12m, 2))).ToList();
        var invested = each * spec.Length;
        var annual = spec.Sum(s => each * s.Apy / 100m);
        var blended = invested > 0 ? MoneyRounding.Round(annual / invested * 100m, 2) : 0m;
        var savingsAnnual = invested * r.Hysa / 100m;
        return new LadderPlan(rungs, blended, MoneyRounding.Round(annual, 2), MoneyRounding.Round(savingsAnnual, 2), MoneyRounding.Round(annual - savingsAnnual, 2));
    }
}
