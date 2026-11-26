using FlowLedger.Application.Features.Shared;

namespace FlowLedger.Application.Features.HomeAssistant;

/// <summary>
/// Pure rules that turn recurring flows, balances, recent transactions and budget state into Home Assistant events.
/// The default output carries no numbers: titles and messages are worded without amounts, and the numeric fields stay
/// null unless <c>ShareAmounts</c> is on. Every event has a stable dedupe key so a condition fires once, not on every run.
/// </summary>
internal static class HomeAssistantEventDetector
{
    /// <summary>A transaction counts as the flow's payment when it lands within this many days of a scheduled date.</summary>
    public const int PaidWindowDays = 5;

    /// <summary>A payment must be within this fraction of the flow's amount (utilities vary; refunds and other purchases do not match).</summary>
    public const decimal AmountTolerance = 0.35m;

    /// <summary>Shortest letters-only name a transaction is matched on (stops "gas" matching "vegas").</summary>
    public const int MinKeyLength = 4;

    public static IReadOnlyList<HaEvent> Detect(
        DateOnly today,
        IReadOnlyList<HaFlow> flows,
        IReadOnlyList<HaAccountBalance> accounts,
        IReadOnlyList<HaRecentTransaction> recent,
        IReadOnlyList<HaBudgetBreach> budgets,
        decimal? projectedLowBalance,
        HomeAssistantSettings settings)
    {
        var events = new List<HaEvent>();
        var lead = Math.Clamp(settings.LeadDays, 0, 30);

        foreach (var f in flows)
        {
            var subject = settings.IncludeNames ? f.Name : null;
            var label = settings.IncludeNames ? $"\"{f.Name}\"" : (f.IsIncome ? "A paycheck" : "A bill");

            // Upcoming: the next scheduled date inside the lead window that has not already been paid.
            var upcoming = f.Dates.Where(d => d >= today && d <= today.AddDays(lead)).OrderBy(d => d).ToList();
            if (upcoming.Count > 0 && FindPayment(f, recent, upcoming[0], today) is null)
            {
                var due = upcoming[0];
                var days = due.DayNumber - today.DayNumber;
                var when = days switch { 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };
                var kind = f.IsIncome ? HaEventKinds.PaycheckExpected : HaEventKinds.BillDueSoon;
                events.Add(new HaEvent(
                    kind, "info",
                    f.IsIncome ? "Paycheck coming" : "Bill due soon",
                    f.IsIncome ? $"{label} is expected {when}." : $"{label} is due {when}.",
                    $"{kind}:{f.Id}:{due:yyyy-MM-dd}",
                    subject, due, days, settings.ShareAmounts ? f.Amount : null));
            }

            // Paid / landed: a recent matching transaction near a scheduled date (most recent date first).
            foreach (var d in f.Dates.Where(d => Math.Abs(d.DayNumber - today.DayNumber) <= PaidWindowDays + 3).OrderByDescending(d => d))
            {
                var hit = FindPayment(f, recent, d, today);
                if (hit is null)
                {
                    continue;
                }

                var kind = f.IsIncome ? HaEventKinds.PaycheckReceived : HaEventKinds.BillPaid;
                events.Add(new HaEvent(
                    kind, "success",
                    f.IsIncome ? "Paycheck landed" : "Bill paid",
                    f.IsIncome ? $"{label} has landed." : $"{label} was paid.",
                    $"{kind}:{f.Id}:{d:yyyy-MM-dd}",
                    subject, hit.Date, null, settings.ShareAmounts ? hit.Amount : null));
                break;
            }
        }

        var overdrawn = accounts.Where(a => a.IsLiquid && a.Balance < 0).ToList();
        foreach (var a in overdrawn)
        {
            events.Add(new HaEvent(HaEventKinds.Overdraft, "error", "Overdraft alert",
                settings.IncludeNames ? $"\"{a.Name}\" is overdrawn." : "An account is overdrawn.",
                $"{HaEventKinds.Overdraft}:{a.Id}:{today:yyyy-MM-dd}",
                settings.IncludeNames ? a.Name : null, today, null, null, settings.ShareAmounts ? a.Balance : null));
        }

        if (projectedLowBalance is < 0 && overdrawn.Count == 0)
        {
            events.Add(new HaEvent(HaEventKinds.LowBalanceForecast, "warning", "Overdraft risk",
                "Your balance is projected to go negative in the coming days.",
                $"{HaEventKinds.LowBalanceForecast}:{today:yyyy-MM-dd}", null, today, null, null,
                settings.ShareAmounts ? projectedLowBalance : null));
        }

        foreach (var b in budgets)
        {
            var state = b.Over ? "over" : "near";
            events.Add(new HaEvent(HaEventKinds.BudgetAlert, b.Over ? "error" : "warning",
                b.Over ? "Over budget" : "Budget nearly used",
                settings.IncludeNames
                    ? $"{b.Category} is {(b.Over ? "over budget" : "nearly used up")} this month."
                    : $"A budget is {(b.Over ? "over" : "nearly used up")} this month.",
                $"{HaEventKinds.BudgetAlert}:{MerchantKey.From(b.Category)}:{state}:{today:yyyy-MM}",
                settings.IncludeNames ? b.Category : null, today));
        }

        var allowed = settings.Kinds.ToHashSet(StringComparer.Ordinal);
        return events.Where(e => allowed.Contains(e.Kind)).ToList();
    }

    private static HaRecentTransaction? FindPayment(HaFlow f, IReadOnlyList<HaRecentTransaction> recent, DateOnly scheduled, DateOnly today)
    {
        var keys = new[] { f.Name, f.Counterparty }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => MerchantKey.From(s!))
            .Where(k => k.Length >= MinKeyLength)
            .ToArray();
        if (keys.Length == 0)
        {
            return null;
        }

        return recent
            .Where(t => t.IsIncome == f.IsIncome
                && t.Date <= today
                && Math.Abs(t.Date.DayNumber - scheduled.DayNumber) <= PaidWindowDays
                && (f.Amount <= 0 || Math.Abs(t.Amount - f.Amount) <= f.Amount * AmountTolerance))
            .FirstOrDefault(t =>
            {
                var text = MerchantKey.From($"{t.Merchant} {t.Description}");
                return keys.Any(k => text.Contains(k, StringComparison.Ordinal));
            });
    }
}
