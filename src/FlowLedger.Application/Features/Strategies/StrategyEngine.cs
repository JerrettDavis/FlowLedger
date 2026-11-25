using System.Globalization;

namespace FlowLedger.Application.Features.Strategies;

/// <summary>
/// Pure rules engine: turns a snapshot of balances, cash flow and market rates into ranked suggestions.
/// No I/O. Every suggestion carries its evidence and assumptions so the numbers are checkable.
/// </summary>
public static class StrategyEngine
{
    public const string Disclaimer =
        "General educational information based on your data and public benchmark rates, not personalised financial, tax or legal advice. "
        + "Verify rates, fees and FDIC/NCUA insurance limits ($250,000 per depositor, per institution, per ownership category) with the institution before moving money.";

    private const decimal MinAnnualImpact = 25m;
    private const int RecentMonths = 3;

    public static StrategyReport Analyze(StrategyInputs input, DateTimeOffset now)
    {
        var rates = input.Rates;
        var months = input.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
        var recent = months.TakeLast(RecentMonths).ToList();
        var avgSpend = recent.Count == 0 ? 0m : recent.Average(m => m.Spend);
        var avgIncome = recent.Count == 0 ? 0m : recent.Average(m => m.Income);

        var cash = input.Accounts.Where(a => !a.IsLiability && !a.IsInvestment && a.Type is "Checking" or "Savings" or "Cash").ToList();
        var checking = cash.Where(a => a.Type is "Checking" or "Cash").Sum(a => Math.Max(0, a.Balance));
        var savings = cash.Where(a => a.Type == "Savings").Sum(a => Math.Max(0, a.Balance));
        var liquid = checking + savings;

        double? savingsRate = avgIncome > 0 ? (double)((avgIncome - avgSpend) / avgIncome) : null;
        double? emergencyMonths = avgSpend > 0 ? (double)(liquid / avgSpend) : null;

        var list = new List<Suggestion>();
        AddIdleCash(list, input, cash, checking, savings, avgSpend);
        AddEmergencyFund(list, liquid, avgSpend, emergencyMonths);
        AddCdLadder(list, rates, liquid, avgSpend);
        AddSavingsRate(list, savingsRate, avgIncome, avgSpend);
        AddCategorySpikes(list, input);
        AddDebt(list, input, liquid, avgSpend);
        AddHousingAndAuto(list, input);
        AddInvesting(list, liquid, avgSpend, rates);

        var ranked = list
            .OrderByDescending(s => s.Severity)
            .ThenByDescending(s => s.AnnualImpact ?? 0m)
            .ToList();
        return new StrategyReport(now, rates, liquid, Math.Round(avgSpend, 2), Math.Round(avgIncome, 2), savingsRate, emergencyMonths, ranked, Disclaimer);
    }

    private static string Money(decimal v) => v.ToString("C0", CultureInfo.GetCultureInfo("en-US"));

    private static string Pct(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static void AddIdleCash(
        List<Suggestion> list, StrategyInputs input, IReadOnlyList<AccountSnapshot> cash, decimal checking, decimal savings, decimal avgSpend)
    {
        var r = input.Rates;
        // Keep ~1.5 months of spending in checking for bills; the rest is idle. Prefer the user's real APY per account.
        var keep = avgSpend * 1.5m;
        var checkingAccts = cash.Where(a => a.Type is "Checking" or "Cash" && a.Balance > 0).ToList();
        var savingsAccts = cash.Where(a => a.Type == "Savings" && a.Balance > 0).ToList();
        var checkingRate = Weighted(checkingAccts, r.TypicalCheckingApy);
        var idleChecking = Math.Max(0, checking - keep);
        var gainChecking = idleChecking * Math.Max(0, r.Hysa - checkingRate) / 100m;
        // Each savings account earning below the best available rate should move up to it.
        var gainSavings = savingsAccts.Sum(a => a.Balance * Math.Max(0, r.Hysa - (a.Rate ?? r.TypicalSavingsApy)) / 100m);
        var total = gainChecking + gainSavings;
        if (total < MinAnnualImpact)
        {
            return;
        }

        var moving = idleChecking + savingsAccts.Where(a => r.Hysa > (a.Rate ?? r.TypicalSavingsApy)).Sum(a => a.Balance);
        var evidence = new List<string>();
        foreach (var a in cash.Where(a => a.Balance > 1000).OrderByDescending(a => a.Balance).Take(5))
        {
            evidence.Add($"{a.Name} ({a.Type}): {Money(a.Balance)} at " + (a.Rate is { } ar ? $"{Pct(ar)} (your rate)" : "an assumed typical rate"));
        }

        evidence.Add($"Average monthly spending: {Money(avgSpend)}; about {Money(keep)} kept in checking for bills.");
        evidence.Add($"Best-available high-yield savings is about {Pct(r.Hysa)} APY.");
        var assumptions = new List<string> { $"Assumes a {Pct(r.Hysa)} APY, which is variable and can change at any time." };
        var guessed = cash.Where(a => a.Balance > 1000 && a.Rate is null).ToList();
        if (guessed.Count > 0)
        {
            assumptions.Add($"{guessed.Count} account(s) have no rate entered, so typical rates were assumed ({Pct(r.TypicalCheckingApy)} checking, {Pct(r.TypicalSavingsApy)} savings). Enter your real rates for an exact figure.");
        }

        list.Add(new Suggestion(
            "idle-cash-yield", SuggestionKind.Cash,
            total >= 500 ? SuggestionSeverity.Attention : SuggestionSeverity.Opportunity,
            $"Put {Money(moving)} of idle cash to work",
            $"About {Money(moving)} sits in low-interest accounts. A high-yield savings or money market account holds the same money with the same federal insurance and could earn roughly {Money(total)} more per year.",
            Math.Round(total, 0), guessed.Count == 0 ? 0.9 : 0.75, evidence,
            [
                "Open a high-yield savings or money market account at an FDIC- or NCUA-insured institution.",
                "Set up an automatic transfer so checking keeps about 1.5 months of expenses and the rest moves over.",
                "Keep each institution under the $250,000 insurance limit.",
            ],
            assumptions,
            "growth"));
    }

    private static decimal Weighted(IReadOnlyList<AccountSnapshot> accts, decimal fallback)
    {
        var bal = accts.Sum(a => a.Balance);
        return bal <= 0 ? fallback : accts.Sum(a => a.Balance * (a.Rate ?? fallback)) / bal;
    }

    private static void AddEmergencyFund(List<Suggestion> list, decimal liquid, decimal avgSpend, double? months)
    {
        if (months is null || avgSpend <= 0 || months >= 3)
        {
            return;
        }

        var target = avgSpend * 3;
        list.Add(new Suggestion(
            "emergency-fund-low", SuggestionKind.Savings,
            months < 1 ? SuggestionSeverity.Urgent : SuggestionSeverity.Attention,
            "Build your emergency fund",
            $"Your liquid cash covers {months:0.0} months of spending. Aim for 3 to 6 months ({Money(target)} to {Money(avgSpend * 6)}).",
            null, 0.9,
            [$"Liquid cash (checking, savings, cash): {Money(liquid)}", $"Average monthly spending: {Money(avgSpend)}"],
            [$"Close the {Money(target - liquid)} gap to reach 3 months, then continue toward 6.", "Automate a transfer on payday so it builds without effort.", "Hold the fund in a high-yield savings account."],
            ["Spending is the average of the last 3 complete months, excluding transfers and investments."],
            "growth"));
    }

    private static void AddCdLadder(List<Suggestion> list, MarketRates r, decimal liquid, decimal avgSpend)
    {
        // Only money beyond a 6-month emergency reserve plus a month of bills is a ladder candidate.
        var excess = liquid - avgSpend * 7.5m;
        if (avgSpend <= 0 || excess < 3000)
        {
            return;
        }

        var amount = Math.Floor(excess / 100m) * 100m;
        var plan = LadderPlanner.Build(amount, r);
        var gain = plan.Advantage;
        var evidence = new List<string>
        {
            $"Liquid cash {Money(liquid)} minus a 6-month reserve and a month of bills leaves about {Money(excess)}.",
            $"CD yields (estimated): 6-mo {Pct(r.Cd6Month)}, 1-yr {Pct(r.Cd1Year)}, 2-yr {Pct(r.Cd2Year)}, 5-yr {Pct(r.Cd5Year)}; high-yield savings {Pct(r.Hysa)}.",
        };
        if (gain < MinAnnualImpact)
        {
            // Flat/inverted curve: a ladder adds only lock-in, so say so instead of recommending it.
            list.Add(new Suggestion(
                "cd-ladder-flat", SuggestionKind.Cash, SuggestionSeverity.Info,
                "A CD ladder doesn't beat savings right now",
                $"CD rates are about the same as or below high-yield savings ({Pct(plan.BlendedApy)} blended vs {Pct(r.Hysa)}), so locking money up adds no reward. Revisit when longer terms pay more.",
                null, 0.7, evidence,
                ["Keep the surplus in high-yield savings or a money market account.", "Re-check when 1-5 year CD rates rise above savings rates."],
                ["Rates are estimates derived from Treasury yields."], "ladder"));
            return;
        }

        list.Add(new Suggestion(
            "cd-ladder", SuggestionKind.Cash, SuggestionSeverity.Opportunity,
            $"Consider a CD ladder with {Money(amount)}",
            $"Splitting {Money(amount)} across {plan.Rungs.Count} CDs that mature at staggered dates blends to about {Pct(plan.BlendedApy)} APY (vs {Pct(r.Hysa)} in savings) while a rung frees up regularly. That is roughly {Money(gain)} more per year.",
            Math.Round(gain, 0), 0.6, evidence,
            [
                .. plan.Rungs.Select(x => $"{Money(x.Amount)} into a {x.Label} CD at about {Pct(x.Apy)}"),
                "When a rung matures, reinvest it in the longest term to keep the ladder rolling, or spend it if you need the cash.",
                "Check early-withdrawal penalties, and prefer non-callable CDs.",
            ],
            ["CD rates are estimated from Treasury yields; real offers vary by institution. Money is locked until maturity (early-withdrawal penalties apply)."],
            "ladder"));
    }

    private static void AddSavingsRate(List<Suggestion> list, double? rate, decimal avgIncome, decimal avgSpend)
    {
        if (rate is null || avgIncome <= 0)
        {
            return;
        }

        var evidence = new List<string> { $"Average monthly income {Money(avgIncome)} vs spending {Money(avgSpend)} (last {RecentMonths} complete months)." };
        if (rate < 0)
        {
            list.Add(new Suggestion(
                "negative-savings-rate", SuggestionKind.Budget, SuggestionSeverity.Urgent,
                "You're spending more than you earn",
                $"Spending exceeds income by about {Money(avgSpend - avgIncome)} a month ({rate:P0} savings rate). That drains savings or builds debt.",
                null, 0.85, evidence,
                ["Review the largest categories on the Spending page for cuts.", "List subscriptions and recurring bills you can pause.", "Use the what-if simulator to see the effect of cutting a category."],
                ["Income and spending exclude transfers and investment contributions."], "past"));
        }
        else if (rate < 0.10)
        {
            var gap = avgIncome * 0.15m - (avgIncome - avgSpend);
            list.Add(new Suggestion(
                "low-savings-rate", SuggestionKind.Budget, SuggestionSeverity.Attention,
                "Raise your savings rate toward 15%",
                $"You keep {rate:P0} of income. Reaching 15% means finding about {Money(gap)} per month, {Money(gap * 12)} a year.",
                Math.Round(gap * 12, 0), 0.75, evidence,
                ["Pick the one or two categories with the largest growth and trim them.", "Automate the saved amount on payday."],
                ["15% is a common rule of thumb, not a requirement."], "past"));
        }
    }

    private static void AddCategorySpikes(List<Suggestion> list, StrategyInputs input)
    {
        var months = input.Months.Select(m => m.Month).OrderBy(m => m, StringComparer.Ordinal).ToList();
        if (months.Count < 4)
        {
            return;
        }

        var last = months[^1];
        var prior = months.Skip(Math.Max(0, months.Count - 7)).SkipLast(1).ToList();
        var found = new List<(string Cat, decimal Last, decimal Avg)>();
        foreach (var (cat, byMonth) in input.CategoryByMonth)
        {
            var avg = prior.Average(p => byMonth.TryGetValue(p, out var v) ? v : 0m);
            var cur = byMonth.TryGetValue(last, out var c) ? c : 0m;
            if (avg >= 25m && cur > avg * 1.3m && cur - avg >= 50m)
            {
                found.Add((cat, cur, avg));
            }
        }

        foreach (var (cat, cur, avg) in found.OrderByDescending(f => f.Last - f.Avg).Take(3))
        {
            var over = cur - avg;
            list.Add(new Suggestion(
                $"category-spike:{cat}", SuggestionKind.Budget, over >= 200 ? SuggestionSeverity.Attention : SuggestionSeverity.Info,
                $"{cat} spending is up {over / avg:P0}",
                $"Last month's {cat} was {Money(cur)} against a typical {Money(avg)}. If that persisted it would cost about {Money(over * 12)} more per year.",
                Math.Round(over * 12, 0), 0.65,
                [$"{cat}: {Money(cur)} last month vs {Money(avg)} average of the previous {prior.Count} months."],
                [$"Open Spending and drill into {cat} to see which transactions drove it.", "Decide whether it was one-off or a new habit; set a budget if it is a habit."],
                ["Compares the last complete month with the average of the months before it."], "past"));
        }
    }

    private static void AddDebt(List<Suggestion> list, StrategyInputs input, decimal liquid, decimal avgSpend)
    {
        var r = input.Rates;
        var cards = input.Accounts.Where(a => a.Type == "CreditCard" && Math.Abs(a.Balance) >= 100).ToList();
        var owed = cards.Sum(a => Math.Abs(a.Balance));
        if (owed <= 0)
        {
            return;
        }

        decimal AprOf(AccountSnapshot a) => a.Rate ?? r.CreditCard;
        var annualInterest = cards.Sum(a => Math.Abs(a.Balance) * AprOf(a) / 100m);
        var evidence = cards.OrderByDescending(a => Math.Abs(a.Balance)).Take(5)
            .Select(a => $"{a.Name}: {Money(Math.Abs(a.Balance))} owed at {Pct(AprOf(a))}" + (a.Rate is null ? " (assumed market average)" : " (your rate)")).ToList();
        evidence.Add($"Fixed-rate personal loans for consolidation average about {Pct(r.PersonalLoan)}.");
        var unknown = cards.Any(a => a.Rate is null);
        var spare = liquid - avgSpend * 3;
        if (spare >= owed)
        {
            var net = annualInterest - owed * r.Hysa / 100m;
            list.Add(new Suggestion(
                "pay-off-cards-with-cash", SuggestionKind.Debt, SuggestionSeverity.Attention,
                $"Pay off {Money(owed)} of card balances from cash",
                $"Cash earning about {Pct(r.Hysa)} is sitting beside debt costing about {Money(annualInterest)} a year in interest. Clearing the cards nets roughly {Money(net)} a year and still leaves a 3-month reserve.",
                Math.Round(net, 0), unknown ? 0.7 : 0.9, evidence,
                ["Pay the highest-APR card first.", "Keep at least 3 months of expenses in cash.", "Then put one bill on a card and autopay the full balance."],
                ["If you already pay cards in full each month there is no interest to save; balances here may be statement balances."
                    + (unknown ? " Some APRs are assumed; enter your real APRs for an exact figure." : string.Empty)], "growth"));
        }
        else if (owed >= 1000)
        {
            var saving = cards.Sum(a => Math.Max(0, Math.Abs(a.Balance) * (AprOf(a) - r.PersonalLoan) / 100m));
            if (saving >= MinAnnualImpact)
            {
                list.Add(new Suggestion(
                    "consolidate-card-debt", SuggestionKind.Debt, SuggestionSeverity.Opportunity,
                    $"Consolidate {Money(owed)} of card debt",
                    $"Moving card balances to a lower-rate vehicle (a 0% balance-transfer card or a fixed-rate personal loan at about {Pct(r.PersonalLoan)}) could save about {Money(saving)} a year. Pair it with a payoff plan so the balance actually falls.",
                    Math.Round(saving, 0), unknown ? 0.5 : 0.8, evidence,
                    ["Compare a 0% balance-transfer card (note the 3-5% transfer fee and promo end date) with a fixed personal loan.", "Use the Debt Payoff page to choose avalanche or snowball.", "Don't re-run up the cards you pay off."],
                    [unknown ? "Some APRs are assumed from market averages; enter your real APRs for an exact comparison." : "Based on the APRs you entered.", "Qualifying rates depend on your credit."], "debt"));
            }
        }
    }

    private static void AddHousingAndAuto(List<Suggestion> list, StrategyInputs input)
    {
        var r = input.Rates;
        foreach (var m in input.Accounts.Where(a => a.Type == "Mortgage" && Math.Abs(a.Balance) >= 20_000))
        {
            var bal = Math.Abs(m.Balance);
            if (m.Rate is { } rate)
            {
                // Real numbers: closing costs ~2% of the balance, new 30-year at today's market rate, same remaining horizon for the old loan.
                var remaining = m.RemainingMonths ?? 300;
                var sim = Simulators.Refinance(bal, rate, remaining, r.Mortgage30, 360, Math.Round(bal * 0.02m, 0));
                var evidence = new List<string>
                {
                    $"{m.Name}: {Money(bal)} at {Pct(rate)} (your rate), {remaining} months left.",
                    $"Market 30-year fixed: {Pct(r.Mortgage30)}. Estimated closing costs {Money(sim.ClosingCosts)} (2%), rolled into the new loan.",
                    $"Payment {Money(sim.CurrentPayment)} to {Money(sim.NewPayment)}; remaining interest {Money(sim.CurrentRemainingInterest)} to {Money(sim.NewTotalInterest)}.",
                };
                if (sim.Worthwhile)
                {
                    list.Add(new Suggestion(
                        $"mortgage-refi:{m.Id}", SuggestionKind.Housing, SuggestionSeverity.Opportunity,
                        $"Refinancing {m.Name} looks worthwhile",
                        $"At {Pct(r.Mortgage30)} versus your {Pct(rate)}, a refinance cuts the payment by about {Money(sim.MonthlySavings)} a month, breaks even in about {sim.BreakEvenMonths} months and saves about {Money(sim.LifetimeSavings)} of interest overall.",
                        Math.Round(sim.MonthlySavings * 12, 0), 0.7, evidence,
                        ["Get 3 written quotes (lender fees, points and APR) and compare against the 2% assumption.", "Only proceed if you'll keep the loan beyond the break-even point.", "Compare lifetime interest, since restarting a 30-year term can raise it."],
                        ["Market rate excludes points and your credit/LTV adjustments.", "Closing costs are estimated at 2% of the balance."], "refinance"));
                }
                else
                {
                    list.Add(new Suggestion(
                        $"mortgage-refi-no:{m.Id}", SuggestionKind.Housing, SuggestionSeverity.Info,
                        $"No refinance advantage for {m.Name} right now",
                        $"Your {Pct(rate)} is {(rate <= r.Mortgage30 ? "at or below" : "not far enough above")} the current {Pct(r.Mortgage30)} market rate to recover closing costs.",
                        null, 0.7, evidence, ["Re-check if market rates fall about 0.75 point below your rate."],
                        ["Based on the rate you entered and an estimated 2% closing cost."], "refinance"));
                }
            }
            else
            {
                list.Add(new Suggestion(
                    $"mortgage-refi-check:{m.Id}", SuggestionKind.Housing, SuggestionSeverity.Info,
                    $"Check whether refinancing {m.Name} pays off",
                    $"Current 30-year fixed rates average about {Pct(r.Mortgage30)} (15-year about {Pct(r.Mortgage15)}). Enter your loan's rate below for an exact break-even.",
                    null, 0.4,
                    [$"Mortgage balance: {Money(bal)}", $"Market: 30-yr {Pct(r.Mortgage30)}, 15-yr {Pct(r.Mortgage15)}"],
                    ["Enter your current rate and months remaining on this page.", "Run the refinance calculator with real quotes' closing costs."],
                    ["Your rate isn't recorded yet. Market rates are Freddie Mac weekly averages and exclude points and fees."], "refinance"));
            }
        }

        foreach (var a in input.Accounts.Where(a => a.Type == "Loan" && Math.Abs(a.Balance) >= 3000
            && (a.Name.Contains("auto", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("car", StringComparison.OrdinalIgnoreCase))))
        {
            var bal = Math.Abs(a.Balance);
            if (a.Rate is { } rate && rate > r.Auto48Month + 1m)
            {
                var months = a.RemainingMonths ?? 48;
                var sim = Simulators.Refinance(bal, rate, months, r.Auto48Month, months, 0m);
                list.Add(new Suggestion(
                    $"auto-refi:{a.Id}", SuggestionKind.Debt, SuggestionSeverity.Opportunity,
                    $"Refinance {a.Name} at a lower rate",
                    $"Your {Pct(rate)} is above the {Pct(r.Auto48Month)} market average. Refinancing over the same {months} months would save about {Money(sim.MonthlySavings)} a month and {Money(sim.LifetimeSavings)} of interest.",
                    Math.Round(sim.MonthlySavings * 12, 0), 0.65,
                    [$"{a.Name}: {Money(bal)} at {Pct(rate)} (your rate)", $"Market car-loan average {Pct(r.Auto48Month)}"],
                    ["Get quotes from a credit union and an online lender.", "Keep the same term; don't extend it just to lower the payment.", "Check the current loan for prepayment penalties."],
                    ["Qualifying rate depends on your credit and the vehicle's age and value."], "refinance"));
            }
            else if (a.Rate is null)
            {
                list.Add(new Suggestion(
                    $"auto-refi-check:{a.Id}", SuggestionKind.Debt, SuggestionSeverity.Info,
                    $"Compare {a.Name} with the market",
                    $"Car loans average about {Pct(r.Auto48Month)} APR. Enter your loan's APR to see whether a refinance would help.",
                    null, 0.4, [$"{a.Name}: {Money(bal)}"],
                    ["Enter the APR and months remaining on this page.", "Get quotes from a credit union and an online lender."],
                    ["Detected from loan account names containing 'auto' or 'car'."], "refinance"));
            }
        }
    }

    private static void AddInvesting(List<Suggestion> list, decimal liquid, decimal avgSpend, MarketRates r)
    {
        var surplus = liquid - avgSpend * 12m;
        if (avgSpend <= 0 || surplus < 10_000)
        {
            return;
        }

        list.Add(new Suggestion(
            "long-term-surplus", SuggestionKind.Investing, SuggestionSeverity.Info,
            $"{Money(surplus)} beyond a year of expenses is in cash",
            "Money you won't need for 5+ years has historically grown faster in diversified investments, though with real risk of loss. Tax-advantaged accounts (401(k) match, IRA, HSA) usually come first.",
            null, 0.4,
            [$"Liquid cash {Money(liquid)} vs 12 months of spending {Money(avgSpend * 12)}."],
            ["Capture any employer 401(k) match before anything else.", "Consider IRA/HSA contributions, then broad low-cost index funds.", "Use the growth simulator to compare outcomes at different returns."],
            [$"Cash alternatives top out near {Pct(r.Hysa)}; investment returns are not guaranteed and this is not a recommendation to buy a security."], "growth"));
    }
}
