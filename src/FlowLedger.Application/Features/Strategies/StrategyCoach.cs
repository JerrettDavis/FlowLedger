using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Strategies;

/// <summary>Where the "do this first" pick came from.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum PickSource { Rules, LocalModel }

public sealed record CoachPlan(
    string Headline,
    string? PriorityId,
    PickSource PickedBy,
    IReadOnlyList<string> Narrative,
    IReadOnlyList<string> Order);

/// <summary>One point of the projected liquid-cash path.</summary>
public sealed record TrajectoryPoint(int MonthsAhead, decimal Expected, decimal Low, decimal High);

public sealed record CashTrajectory(
    decimal MonthlyNetTrend,
    decimal MonthlyNetAverage,
    string Direction,
    decimal? RunwayMonths,
    int? MonthsToEmergencyTarget,
    IReadOnlyList<TrajectoryPoint> Points,
    string Summary);

/// <summary>
/// The AI-assisted layer. The local model (ADR 0013) is grammar-constrained to choose among options, so it can only pick which
/// engine-generated suggestion to do first; it cannot write text or alter numbers. Everything shown is built from engine facts.
/// Without a model the same pick is made by a transparent score, so results are deterministic and the feature never depends on AI.
/// </summary>
public static class StrategyCoach
{
    private const int MaxCandidates = 5;
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(20);

    public static async Task<CoachPlan> PlanAsync(StrategyReport report, ILocalLlm? llm, CancellationToken ct = default)
    {
        var ordered = report.Suggestions.OrderByDescending(Score).ToList();
        if (ordered.Count == 0)
        {
            return new CoachPlan("You're in good shape: nothing needs action right now.", null, PickSource.Rules,
                ["Cash, savings rate and debt look healthy against current rates. Re-check after your next sync or when rates move."], []);
        }

        var candidates = ordered.Take(MaxCandidates).ToList();
        var pick = candidates[0];
        var by = PickSource.Rules;
        if (llm is { State: { Enabled: true, ModelInstalled: true } } && candidates.Count > 1)
        {
            // The model sees only engine-owned labels (kind, severity, rule id, rounded impact), never user-entered text.
            var choices = candidates.Select(c => $"{c.Kind}/{c.Severity}: {RuleName(c.Id)}" + (c.AnnualImpact is { } i ? $" (about ${i:0} per year)" : "")).ToList();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ModelTimeout);
            int? idx = null;
            try
            {
                idx = await llm.ChooseAsync(
                "You are a careful personal-finance coach. Pick the single action that a typical household should do FIRST: " +
                "safety (emergency fund, urgent overspending, high-interest debt) before optimisation (yield, ladders), " +
                "and larger annual impact before smaller. Answer with the option number only.",
                $"Household cash covers {report.EmergencyMonths?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unknown"} months of spending; "
                + $"savings rate {(report.SavingsRate is { } r ? r.ToString("P0", CultureInfo.InvariantCulture) : "unknown")}. Which action first?",
                choices, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cold model load took too long: keep the rules-based pick rather than stall the page.
            }

            if (idx is { } i2 && i2 >= 0 && i2 < candidates.Count)
            {
                pick = candidates[i2];
                by = PickSource.LocalModel;
            }
        }

        // Safety guard: a model must never push an optimisation ahead of an urgent safety item.
        var urgent = candidates.FirstOrDefault(c => c.Severity == SuggestionSeverity.Urgent);
        if (urgent is not null && pick.Severity < SuggestionSeverity.Urgent)
        {
            pick = urgent;
            by = PickSource.Rules;
        }

        var order = new[] { pick }.Concat(ordered.Where(s => s.Id != pick.Id)).Select(s => s.Id).ToList();
        var total = report.Suggestions.Where(s => s.AnnualImpact is > 0).Sum(s => s.AnnualImpact!.Value);
        var narrative = new List<string>
        {
            $"Start with: {pick.Title}. {pick.Summary}",
        };
        if (pick.Steps.Count > 0)
        {
            narrative.Add("First step: " + pick.Steps[0]);
        }

        var rest = order.Skip(1).Take(2).Select(id => report.Suggestions.First(s => s.Id == id).Title).ToList();
        if (rest.Count > 0)
        {
            narrative.Add("Then: " + string.Join("; ", rest) + ".");
        }

        if (total > 0)
        {
            narrative.Add($"Together the suggestions with a dollar estimate are worth about {total.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} per year at today's rates (estimates, not guarantees).");
        }

        return new CoachPlan(pick.Title, pick.Id, by, narrative, order);
    }

    /// <summary>Transparent priority: severity dominates, then dollar impact, then confidence.</summary>
    public static double Score(Suggestion s) =>
        (int)s.Severity * 100_000d + (double)Math.Min(s.AnnualImpact ?? 0m, 50_000m) + s.Confidence * 10;

    private static string RuleName(string id) => id.Split(':', 2)[0] switch
    {
        "idle-cash-yield" => "move idle cash to a higher-yield insured account",
        "emergency-fund-low" => "build the emergency fund",
        "cd-ladder" => "build a CD ladder",
        "cd-ladder-flat" => "CD ladder not worthwhile now",
        "negative-savings-rate" => "spending exceeds income",
        "low-savings-rate" => "raise the savings rate",
        "category-spike" => "a spending category jumped",
        "pay-off-cards-with-cash" => "pay off credit cards from cash",
        "consolidate-card-debt" => "consolidate credit card debt",
        "mortgage-refi" => "refinance the mortgage (looks worthwhile)",
        "mortgage-refi-no" => "mortgage refinance not worthwhile now",
        "auto-refi" => "refinance the car loan at a lower rate",
        "mortgage-refi-check" => "check a mortgage refinance",
        "auto-refi-check" => "check a car loan refinance",
        "long-term-surplus" => "consider long-term investing of surplus cash",
        var other => other,
    };
}

/// <summary>Least-squares trend on monthly net cash flow, projected forward with a widening uncertainty band.</summary>
public static class CashTrajectoryProjector
{
    public static CashTrajectory Project(IReadOnlyList<MonthFlow> months, decimal liquid, decimal avgSpend, int horizon = 12)
    {
        var ordered = months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
        if (ordered.Count < 3)
        {
            return new CashTrajectory(0, 0, "unknown", null, null, [], "Not enough history yet: at least 3 complete months are needed to project.");
        }

        var y = ordered.Select(m => (double)(m.Income - m.Spend)).ToArray();
        var n = y.Length;
        var xMean = (n - 1) / 2.0;
        var yMean = y.Average();
        var sxx = y.Select((_, i) => (i - xMean) * (i - xMean)).Sum();
        var slope = sxx == 0 ? 0 : y.Select((v, i) => (i - xMean) * (v - yMean)).Sum() / sxx;
        var resid = y.Select((v, i) => v - (yMean + slope * (i - xMean))).ToArray();
        var sigma = n > 2 ? Math.Sqrt(resid.Sum(r => r * r) / (n - 2)) : 0;

        // Damp the trend: extrapolating a slope across a year overstates; blend halfway toward the average.
        double NetAt(int k) => yMean + slope * 0.5 * (n - 1 + k - xMean);

        var points = new List<TrajectoryPoint> { new(0, liquid, liquid, liquid) };
        double bal = (double)liquid, cum = 0;
        for (var k = 1; k <= horizon; k++)
        {
            var net = NetAt(k);
            bal += net;
            cum += sigma * sigma;
            var band = 1.28 * Math.Sqrt(cum); // ~80% interval, independent monthly errors
            points.Add(new TrajectoryPoint(k, MoneyRounding.Round((decimal)bal, 0), MoneyRounding.Round((decimal)(bal - band), 0), MoneyRounding.Round((decimal)(bal + band), 0)));
        }

        var nextNet = (decimal)NetAt(1);
        var direction = slope > 50 ? "improving" : slope < -50 ? "worsening" : "steady";
        decimal? runway = nextNet < 0 ? MoneyRounding.Round(liquid / -nextNet, 1) : null;
        int? toTarget = null;
        var target = avgSpend * 3;
        if (liquid >= target)
        {
            toTarget = 0;
        }
        else if (nextNet > 0)
        {
            toTarget = (int)Math.Ceiling((target - liquid) / nextNet);
            if (toTarget > 600)
            {
                toTarget = null;
            }
        }

        var money = (decimal v) => v.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
        var summary = nextNet < 0
            ? $"At this trend you save {money(nextNet)} a month (negative), so cash would last about {runway:0.#} months. Direction: {direction}."
            : toTarget == 0
                ? $"Cash flow is {direction}: about {money(nextNet)} a month net; your 3-month emergency target is already covered."
                : $"Cash flow is {direction}: about {money(nextNet)} a month net" + (toTarget is { } t ? $", reaching a 3-month emergency fund in about {t} months." : ".");
        return new CashTrajectory(MoneyRounding.Round((decimal)slope, 2), MoneyRounding.Round((decimal)yMean, 2), direction, runway, toTarget, points, summary);
    }
}
