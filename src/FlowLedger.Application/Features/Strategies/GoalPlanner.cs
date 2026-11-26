using FlowLedger.Domain.ValueObjects;
namespace FlowLedger.Application.Features.Strategies;

public sealed record GoalDebt(string Name, decimal Balance, decimal RatePercent, int RemainingMonths);

public sealed record GoalContext(decimal AvgMonthlyIncome, decimal AvgMonthlySpend, IReadOnlyList<GoalDebt> Debts);

public sealed record GoalRequest(
    decimal Target, decimal Saved, decimal Monthly, decimal Apy, int DeadlineMonths,
    decimal ExtraMonthly, decimal IncomeRaisePct, decimal SpendCutPct, decimal LumpSum, decimal ScenarioApy);

public sealed record GoalLever(
    string Id, string Title, string Detail, int? MonthsToGoal, int? MonthsSaved, decimal? MonthlyImpact);

public sealed record GoalPlan(
    decimal Target, int? BaselineMonths, int? ScenarioMonths, int? MonthsSaved,
    decimal? RequiredMonthly, decimal? Shortfall, decimal Surplus, decimal ScenarioMonthly,
    IReadOnlyList<decimal> Baseline, IReadOnlyList<decimal> Scenario,
    IReadOnlyList<GoalLever> Levers, IReadOnlyList<string> Callouts);

/// <summary>"How do I get to $X?" Simulates the current path, a slider-driven scenario, and each lever on its own.</summary>
public static class GoalPlanner
{
    public const int MaxMonths = Simulators.MaxMonths;

    /// <summary>Month-by-month balances (index = month). <paramref name="boostAfter"/> adds <paramref name="boost"/>/mo from that month on.</summary>
    public static List<decimal> Path(decimal start, decimal monthly, decimal apy, int months, decimal boost = 0m, int boostAfter = 0)
    {
        var path = new List<decimal>(months + 1) { start };
        var bal = start;
        var rate = Simulators.MonthlyRate(apy);
        for (var m = 1; m <= months; m++)
        {
            bal += bal * rate + monthly + (m > boostAfter ? boost : 0m);
            path.Add(bal);
        }

        return path;
    }

    public static int? MonthsToReach(decimal target, decimal start, decimal monthly, decimal apy, decimal boost = 0m, int boostAfter = 0)
    {
        if (start >= target)
        {
            return 0;
        }

        var bal = start;
        var rate = Simulators.MonthlyRate(apy);
        for (var m = 1; m <= MaxMonths; m++)
        {
            bal += bal * rate + monthly + (m > boostAfter ? boost : 0m);
            if (bal >= target)
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>Monthly deposit needed to hit <paramref name="target"/> in <paramref name="months"/> (end-of-month deposits).</summary>
    public static decimal RequiredMonthly(decimal target, decimal start, decimal apy, int months)
    {
        if (months <= 0)
        {
            return Math.Max(0, target - start);
        }

        var r = Simulators.MonthlyRate(apy);
        if (r == 0)
        {
            var linear = target - start;
            return linear <= 0 ? 0m : MoneyRounding.Round(linear / months, 2);
        }

        // deposit = (target - start * g) * r / (g - 1) with g = (1 + r)^months. Multiplying through by v^months (v = 1 / (1 + r))
        // keeps every term below 1, so it stays in decimal and cannot overflow however long the horizon or high the rate.
        var vn = LoanMath.PowInt(1m / (1m + r), months);
        var need = target * vn - start;
        if (need <= 0)
        {
            return 0m;
        }

        return MoneyRounding.Round(need * r / (1m - vn), 2);
    }

    public static GoalPlan Plan(GoalRequest q, GoalContext ctx, MarketRates rates)
    {
        var target = Math.Max(1m, q.Target);
        var saved = Math.Max(0m, q.Saved);
        var monthly = Math.Max(0m, q.Monthly);
        var apy = q.Apy;
        var income = Math.Max(0m, ctx.AvgMonthlyIncome);
        var spend = Math.Max(0m, ctx.AvgMonthlySpend);
        var surplus = income - spend;

        var baseMonths = MonthsToReach(target, saved, monthly, apy);

        var scMonthly = monthly + Math.Max(0, q.ExtraMonthly)
            + income * Math.Max(0, q.IncomeRaisePct) / 100m + spend * Math.Max(0, q.SpendCutPct) / 100m;
        var scStart = saved + Math.Max(0, q.LumpSum);
        var scApy = q.ScenarioApy;
        var scMonths = MonthsToReach(target, scStart, scMonthly, scApy);
        int? monthsSaved = baseMonths is { } b && scMonths is { } s ? b - s : null;

        // Chart horizon: far enough to show both reaching the goal and the deadline.
        var horizon = Math.Max(q.DeadlineMonths, Math.Max(baseMonths ?? 0, scMonths ?? 0));
        if (horizon == 0 || (baseMonths is null && scMonths is null))
        {
            horizon = Math.Max(horizon, 120);
        }

        horizon = Math.Min(MaxMonths, (int)(horizon * 1.1) + 1);
        var basePath = Path(saved, monthly, apy, horizon);
        var scPath = Path(scStart, scMonthly, scApy, horizon);

        decimal? required = null, shortfall = null;
        if (q.DeadlineMonths > 0)
        {
            required = RequiredMonthly(target, scStart, scApy, q.DeadlineMonths);
            shortfall = Math.Max(0, required.Value - scMonthly);
        }

        var levers = BuildLevers(target, saved, monthly, apy, baseMonths, income, spend, rates, ctx.Debts);
        var callouts = BuildCallouts(q, target, baseMonths, required, shortfall, scMonthly, surplus, income, spend, apy, rates, ctx.Debts, levers);

        return new GoalPlan(target, baseMonths, scMonths, monthsSaved, required, shortfall, MoneyRounding.Round(surplus, 2), MoneyRounding.Round(scMonthly, 2),
            basePath.Select(x => MoneyRounding.Round(x, 2)).ToList(), scPath.Select(x => MoneyRounding.Round(x, 2)).ToList(), levers, callouts);
    }

    private static List<GoalLever> BuildLevers(
        decimal target, decimal saved, decimal monthly, decimal apy, int? baseMonths, decimal income, decimal spend,
        MarketRates rates, IReadOnlyList<GoalDebt> debts)
    {
        var list = new List<GoalLever>();

        GoalLever Make(string id, string title, string detail, int? months, decimal? impact) =>
            new(id, title, detail, months, baseMonths is { } b && months is { } m ? b - m : null, impact);

        foreach (var add in new[] { 100m, 250m, 500m })
        {
            list.Add(Make($"save-{add:0}", $"Save {Money(add)} more per month",
                "Automate the transfer on payday so it happens before spending.",
                MonthsToReach(target, saved, monthly + add, apy), add));
        }

        if (spend > 0)
        {
            var cut = MoneyRounding.Round(spend * 0.10m, 0);
            list.Add(Make("cut-10", $"Trim spending 10% (about {Money(cut)}/mo)",
                "Start with the largest discretionary categories on the Spending page.",
                MonthsToReach(target, saved, monthly + cut, apy), cut));
        }

        if (income > 0)
        {
            foreach (var pct in new[] { 5m, 10m })
            {
                var extra = MoneyRounding.Round(income * pct / 100m, 0);
                list.Add(Make($"income-{pct:0}", $"Earn {pct:0}% more (about {Money(extra)}/mo), saving all of it",
                    "A raise, side income or extra shifts. Assumes the whole increase goes to this goal.",
                    MonthsToReach(target, saved, monthly + extra, apy), extra));
            }
        }

        if (apy + 0.25m < rates.Hysa)
        {
            list.Add(Make("yield-hysa", $"Move savings to a high-yield account (~{rates.Hysa:0.##}% vs {apy:0.##}%)",
                "Same money, no extra effort; FDIC/NCUA-insured.",
                MonthsToReach(target, saved, monthly, rates.Hysa), null));
        }

        if ((baseMonths ?? MaxMonths) >= 60)
        {
            list.Add(Make("reinvest", "Invest it long-term instead (assumes about 7%/yr, with real risk)",
                "Only for goals 5+ years out. Returns are not guaranteed and can be negative in any year.",
                MonthsToReach(target, saved, monthly, 7m), null));
        }

        foreach (var d in debts.Where(d => d.Balance > 0 && d.RatePercent > 0).OrderByDescending(d => d.RatePercent).Take(3))
        {
            var after = Math.Max(1, d.RemainingMonths);
            var freed = LoanMath.Payment(d.Balance, d.RatePercent, after);
            var better = d.RatePercent > apy
                ? $" At {d.RatePercent:0.##}% APR it costs more than savings earn ({apy:0.##}%), so paying it off is a guaranteed return."
                : "";
            list.Add(Make($"debt-{d.Name}", $"Pay off {d.Name} ({Money(d.Balance)} at {d.RatePercent:0.##}%), then redirect about {Money(freed)}/mo",
                $"Frees its payment after about {after} months.{better}",
                MonthsToReach(target, saved, monthly, apy, freed, after), freed));
        }

        return list.OrderByDescending(l => l.MonthsSaved ?? int.MinValue).ToList();
    }

    private static List<string> BuildCallouts(
        GoalRequest q, decimal target, int? baseMonths, decimal? required, decimal? shortfall, decimal scMonthly,
        decimal surplus, decimal income, decimal spend, decimal apy, MarketRates rates, IReadOnlyList<GoalDebt> debts, List<GoalLever> levers)
    {
        var c = new List<string>();
        if (baseMonths is null)
        {
            c.Add($"At your current pace you won't reach {Money(target)} within 50 years. Raise monthly savings or lower the goal.");
        }

        if (q.DeadlineMonths > 0 && required is { } req)
        {
            c.Add($"To hit {Money(target)} in {q.DeadlineMonths / 12.0:0.#} years you need about {Money(req)}/month in this scenario.");
            if (shortfall is > 0)
            {
                c.Add($"That is {Money(shortfall.Value)}/month more than the scenario saves.");
                if (income > 0)
                {
                    var spendPart = spend > 0 ? $", or cutting about {shortfall.Value / spend:P0} of spending" : "";
                    c.Add($"Closing it with income alone means roughly {shortfall.Value / income:P0} more income{spendPart}, or a mix.");
                }
            }
            else
            {
                c.Add("This scenario meets the deadline.");
            }
        }

        if (income > 0 && surplus <= 0)
        {
            c.Add($"You currently spend about as much as you earn (surplus {Money(surplus)}/mo). Contributions have to come from more income or lower spending.");
        }
        else if (scMonthly > surplus * 1.05m && surplus > 0)
        {
            c.Add($"This scenario saves {Money(scMonthly)}/mo, more than your recent surplus of {Money(surplus)}/mo. It needs real changes, not just a transfer.");
        }

        var costly = debts.Where(d => d.Balance > 0 && d.RatePercent > Math.Max(apy, rates.Hysa)).OrderByDescending(d => d.RatePercent).FirstOrDefault();
        if (costly is not null)
        {
            c.Add($"{costly.Name} costs {costly.RatePercent:0.##}% APR, more than any savings account pays. Paying it down first usually beats saving toward this goal.");
        }

        if (levers.FirstOrDefault(l => l.MonthsSaved is > 0) is { } best)
        {
            c.Add($"Biggest single lever: \"{best.Title}\" gets you there {best.MonthsSaved} months sooner.");
        }

        return c;
    }

    private static string Money(decimal v) => v.ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
