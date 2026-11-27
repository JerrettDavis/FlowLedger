using FlowLedger.Application.Features.Shared;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Subscriptions;

/// <summary>A recurring subscription found in the transactions (#70).</summary>
/// <param name="Name">The merchant as the household sees it.</param>
/// <param name="Cadence"><c>Monthly</c> or <c>Yearly</c>.</param>
/// <param name="Status"><c>Active</c> (charged on schedule) or <c>Lapsed</c> (the next charge is well overdue: probably cancelled).</param>
/// <param name="Amount">The latest charge.</param>
/// <param name="MonthlyCost">The cost per month (a yearly charge divided by twelve).</param>
/// <param name="PriceChange"><c>Increase</c>, <c>Decrease</c> or null: the most recent price change, while it is still news (about four months).</param>
/// <param name="PreviousAmount">What the charge was before the price change (null without one).</param>
/// <param name="PriceChangedOn">The date of the first charge at the new price (null without a price change).</param>
public sealed record DetectedSubscription(
    string Name,
    string Cadence,
    string Status,
    decimal Amount,
    decimal MonthlyCost,
    DateOnly FirstCharged,
    DateOnly LastCharged,
    DateOnly NextExpected,
    int Charges,
    string? PriceChange,
    decimal? PreviousAmount,
    DateOnly? PriceChangedOn);

/// <summary>The detected subscriptions and what they cost together.</summary>
public sealed record SubscriptionOverview(IReadOnlyList<DetectedSubscription> Subscriptions, decimal MonthlyTotal, decimal YearlyTotal);

/// <summary>
/// Pure rules that find subscriptions: a merchant charged at a steady monthly (or yearly) rhythm for a steady amount, with a flag
/// when the price moved. Variable bills (utilities, groceries) do not qualify because their amounts are not steady.
/// </summary>
internal static class SubscriptionDetector
{
    /// <summary>Charges needed for a monthly rhythm to count (two in a row could be a coincidence).</summary>
    public const int MinMonthlyCharges = 3;

    /// <summary>A price change stays news for this many days.</summary>
    public const int PriceChangeNewsDays = 120;

    private const decimal SteadyTolerance = 0.15m;
    private const decimal ChangeRatio = 0.03m;
    private const decimal ChangeMinimum = 0.50m;

    public static IReadOnlyList<DetectedSubscription> Detect(IReadOnlyList<TransactionDto> history, IReadOnlySet<Guid> transferIds, DateOnly today)
    {
        static DateOnly Day(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        var debits = nameof(FlowLedger.Domain.Aggregates.TransactionDirection.Debit);
        var found = new List<DetectedSubscription>();

        var groups = history
            .Where(t => t.Direction == debits && t.Amount > 0 && !transferIds.Contains(t.Id)
                && FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsRealStatus(t.Status) && Day(t) <= today)
            .GroupBy(t => MerchantKey.From(NameOf(t)))
            .Where(g => g.Key.Length >= 4);

        foreach (var group in groups)
        {
            var charges = group.OrderBy(Day).ThenBy(t => t.Amount).ToList();
            if (charges.Count < 2)
            {
                continue;
            }

            var gaps = charges.Zip(charges.Skip(1), (a, b) => Day(b).DayNumber - Day(a).DayNumber).ToList();
            var medianGap = Median(gaps.Select(g => (decimal)g).ToList());
            var monthly = medianGap is >= 25 and <= 35 && charges.Count >= MinMonthlyCharges && Share(gaps, 24, 37) >= 0.7m;
            var yearly = medianGap is >= 350 and <= 380;
            if (!monthly && !yearly)
            {
                continue;
            }

            // Steady amounts only: most charges within 15% of the typical one. A utility bill that swings is not a subscription.
            var typical = Median(charges.Select(c => c.Amount).ToList());
            if (typical <= 0 || (decimal)charges.Count(c => Math.Abs(c.Amount - typical) <= typical * SteadyTolerance) / charges.Count < 0.6m)
            {
                continue;
            }

            var latest = charges[^1];
            var cadenceDays = monthly ? 30 : 365;
            var nextExpected = Day(latest).AddDays(cadenceDays);
            var lapsed = today.DayNumber - Day(latest).DayNumber > (monthly ? 50 : 400);
            var (change, previous, changedOn) = LastPriceChange(charges, today);

            found.Add(new DetectedSubscription(
                NameOf(latest), monthly ? "Monthly" : "Yearly", lapsed ? "Lapsed" : "Active", latest.Amount,
                Math.Round(monthly ? latest.Amount : latest.Amount / 12m, 2), Day(charges[0]), Day(latest), nextExpected, charges.Count,
                change, previous, changedOn));
        }

        return found
            .OrderBy(s => s.Status == "Active" ? 0 : 1)
            .ThenByDescending(s => s.MonthlyCost)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The most recent price change among consecutive charges, only if it is recent. A one-off blip that returns to the old amount on the
    /// next charge is not a price change.
    /// </summary>
    private static (string? Change, decimal? Previous, DateOnly? On) LastPriceChange(List<TransactionDto> charges, DateOnly today)
    {
        static DateOnly Day(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        for (var i = charges.Count - 1; i >= 1; i--)
        {
            var before = charges[i - 1].Amount;
            var now = charges[i].Amount;
            if (Math.Abs(now - before) < Math.Max(ChangeMinimum, before * ChangeRatio))
            {
                continue;
            }

            // The end of a blip: the charge before was the odd one out and this one is back to the amount before that.
            if (i >= 2 && Math.Abs(now - charges[i - 2].Amount) < Math.Max(ChangeMinimum, charges[i - 2].Amount * ChangeRatio))
            {
                continue;
            }

            // A blip: the very next charge is back to the old amount.
            if (i + 1 < charges.Count && Math.Abs(charges[i + 1].Amount - before) < Math.Max(ChangeMinimum, before * ChangeRatio))
            {
                continue;
            }

            return today.DayNumber - Day(charges[i]).DayNumber <= PriceChangeNewsDays
                ? (now > before ? "Increase" : "Decrease", before, Day(charges[i]))
                : (null, null, null);
        }

        return (null, null, null);
    }

    private static decimal Share(List<int> gaps, int low, int high) =>
        gaps.Count == 0 ? 0m : (decimal)gaps.Count(g => g >= low && g <= high) / gaps.Count;

    private static decimal Median(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    private static string NameOf(TransactionDto t) => string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName;
}

/// <summary>Lists the household's detected subscriptions with their price changes.</summary>
internal sealed class ListSubscriptionsHandler(
    Abstractions.ITransactionRepository transactions, Abstractions.IAccountRepository accounts, TimeProvider clock)
{
    /// <summary>Two years: enough for two yearly charges.</summary>
    private const int HistoryDays = 800;

    public async Task<SubscriptionOverview> HandleAsync(CancellationToken ct = default)
    {
        var accountList = await accounts.ListAsync(ct);
        var visible = accountList.Where(a => !a.TransactionsHidden).Select(a => a.AccountId).ToList();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (visible.Count == 0)
        {
            return new SubscriptionOverview([], 0m, 0m);
        }

        var dtos = (await transactions.ListForAccountsAsync(visible, today.AddDays(-HistoryDays), today, ct)).Select(TransactionMapper.ToDto).ToList();
        var types = accountList.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var subscriptions = SubscriptionDetector.Detect(dtos, TransferDetector.Detect(dtos, types), today);

        var active = subscriptions.Where(s => s.Status == "Active").ToList();
        var monthly = active.Sum(s => s.MonthlyCost);
        return new SubscriptionOverview(subscriptions, monthly, monthly * 12m);
    }
}
