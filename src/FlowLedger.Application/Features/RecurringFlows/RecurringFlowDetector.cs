using FlowLedger.Application.Features.BillSheet;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>One real, posted transaction considered for recurrence detection.</summary>
public sealed record FlowTxn(Guid AccountId, DateOnly Date, decimal Amount, bool IsCredit, string Text);

/// <summary>A recurring pattern found in the transactions, ready to become a <c>RecurringFlow</c>.</summary>
public sealed record DetectedFlow(
    string Key,
    Guid AccountId,
    string Name,
    string Direction,           // "Debit" | "Credit"
    decimal Amount,             // median observed amount
    string AmountModel,         // "Fixed" | "Estimated"
    string Frequency,           // Weekly | EveryNWeeks | TwiceMonthly | Monthly (RecurrenceFrequency names)
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences,
    double Confidence,
    bool IsPeriodicExtra = false);

/// <summary>
/// Pure detector for recurring income and expenses. Groups transactions by (account, direction, merchant), measures the
/// gaps between occurrences and classifies them as weekly, every-two-weeks, twice-monthly or monthly. High-frequency
/// merchants (fuel, groceries, cafes) never qualify because their gaps are short and irregular; patterns that stopped
/// (a cancelled subscription) are dropped. Confidence blends gap regularity, amount stability and how much history there is.
/// </summary>
public static class RecurringFlowDetector
{
    private const double MinConfidence = 0.6;

    /// <summary>Suffix on the name of the monthly flow that carries a recurring bonus / true-up on top of a weekly or biweekly base.</summary>
    public const string ExtraSuffix = " (periodic extra)";

    // A deposit this far above the typical one is a candidate periodic extra (a monthly bonus week).
    private const decimal ExtraRatio = 1.4m;

    public static IReadOnlyList<DetectedFlow> Detect(IReadOnlyList<FlowTxn> transactions, DateOnly today)
    {
        var result = new List<DetectedFlow>();
        var buckets = transactions
            .Where(t => t.Amount > 0 && !string.IsNullOrWhiteSpace(t.Text))
            .GroupBy(t => (t.AccountId, t.IsCredit));

        foreach (var bucket in buckets)
        {
            // Group by normalized merchant (processor prefix, phone numbers and state codes removed).
            var byNorm = bucket
                .Select(t => (Txn: t, Norm: GroupingName(t)))
                .Where(x => RecurringDetector.Key(x.Norm).Length >= 3)
                .GroupBy(x => x.Norm)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Txn).ToList());

            // The same merchant often appears with and without a trailing city ("MICROSOFT" / "MICROSOFT REDMOND"). Try the
            // shortest key first with every longer key that extends it merged in; if that is not a pattern, the longer keys
            // get their own turn. Merging can only blur a pattern, never invent one.
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in byNorm.Keys.OrderBy(k => k.Count(c => c == ' ')).ThenBy(k => k.Length).ToList())
            {
                if (consumed.Contains(root))
                {
                    continue;
                }

                var members = byNorm.Keys
                    .Where(k => !consumed.Contains(k) && (k == root || k.StartsWith(root + " ", StringComparison.Ordinal)))
                    .ToList();
                var txns = members.SelectMany(k => byNorm[k]).ToList();
                var isCredit = bucket.Key.IsCredit;

                if (!isCredit && IsHabitMerchant(root))
                {
                    foreach (var m in members)
                    {
                        consumed.Add(m);
                    }

                    continue;
                }

                var found = false;
                foreach (var flow in Analyze(bucket.Key.AccountId, isCredit, FlowKey(root), txns, today, root))
                {
                    // A pattern that missed two whole periods has stopped (cancelled subscription, ended job): not worth predicting.
                    var stopped = today.DayNumber - flow.LastSeen.DayNumber > PeriodDays(flow) * 2 + 3;
                    if (!stopped && flow.Confidence >= MinConfidence)
                    {
                        result.Add(flow);
                        found = true;
                    }
                }

                if (found)
                {
                    foreach (var m in members)
                    {
                        consumed.Add(m);
                    }
                }
            }
        }

        return MergeAliases(result).OrderByDescending(f => f.Confidence).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The same bill often reaches the ledger under two spellings ("Adobe CC" / "ADOBE INC ADOBE", "Home Assistant Cloud" /
    /// "NABU CASA"). Two detections on one account with the same direction, cadence, amount (within 3%) and billing day
    /// (within a day, or the same weekday) are one charge: keep the one with the most evidence and the cleaner name.
    /// Periodic extras are never merged with their base flow.
    /// </summary>
    private static List<DetectedFlow> MergeAliases(List<DetectedFlow> flows)
    {
        var kept = new List<DetectedFlow>();
        foreach (var f in flows.OrderByDescending(f => f.Occurrences).ThenByDescending(f => f.Confidence).ThenBy(f => f.Name.Length))
        {
            var same = kept.Any(k => k.AccountId == f.AccountId
                && k.Direction == f.Direction
                && k.Frequency == f.Frequency
                && k.IsPeriodicExtra == f.IsPeriodicExtra
                && Math.Abs(k.Amount - f.Amount) <= Math.Max(0.5m, k.Amount * 0.03m)
                && (f.Frequency == "Monthly"
                    ? k.DayOfMonth is { } kd && f.DayOfMonth is { } fd && Math.Abs(kd - fd) <= 1
                    : f.Frequency is "Weekly" or "EveryNWeeks" && k.AnchorDayOfWeek == f.AnchorDayOfWeek && k.IntervalWeeks == f.IntervalWeeks));
            if (!same)
            {
                kept.Add(f);
            }
        }

        return kept;
    }

    // Merchants people visit out of habit, not on a schedule: fuel, restaurants, groceries, coffee, delivery. Never a bill.
    private static readonly string[] HabitWords =
    [
        "CASEYS", "QUIKTRIP", "QT ", "SHELL", "CHEVRON", "EXXON", "MOBIL ", "MURPHY", "KUM & GO", "KUM AND GO", "LOVES", "SPEEDWAY",
        "CONOCO", "PHILLIPS 66", "SINCLAIR", "SUNOCO", "VALERO", "BUCEE", "BUC-EE", "RACETRAC", "WAWA", "SHEETZ", "IONNA", "CIRCLE K",
        "7-ELEVEN", "7 ELEVEN", "FUEL", "GAS STATION", "PETRO", "TRAVEL CENTER", "CEFCO", "LOVE'S",
        "MCDONALD", "WENDY", "BURGER", "TACO", "PIZZA", "CAFE", "COFFEE", "STARBUCKS", "GRILL", "BBQ", "SUSHI", "DINER", "BAKERY",
        "CHICK-FIL-A", "CHICKFILA", "SONIC", "WHATABURGER", "TORCHY", "CUISINE", "RESTAURANT", "CANTINA", "KITCHEN", "DOORDASH",
        "UBER EATS", "UBEREATS", "GRUBHUB", "DUNKIN", "PANERA", "CHIPOTLE", "SUBWAY", "DAIRY QUEEN", "BREWING", "BREWERY", "PUB ",
        "WALMART", "WAL-MART", "SAFEWAY", "REASOR", "SPROUTS", "WHOLE FOODS", "KROGER", "ALDI", "TARGET", "COSTCO", "SAMS CLUB",
        "SAM'S CLUB", "HOMELAND", "DOLLAR GENERAL", "DOLLAR TREE", "WALGREENS", "CVS", "BASS PRO", "AMAZON MKTP", "AMZN MKTP",
    ];

    // "Transfer to Checking *1436 - Online Banking": the account suffix is what tells one standing transfer from another, and
    // the amount tells a $260 biweekly top-up from a $50 monthly one on the very same account pair. Merchant normalization
    // drops the suffix (it contains digits), so transfers carry their reference and amount into the grouping name.
    private static readonly System.Text.RegularExpressions.Regex TransferText = new(
        @"(transfer|xfer)\s+(to|from)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex AccountRef = new(
        @"[*#x]\s*(\d{3,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private const char TransferMarker = '§';

    public static bool IsTransferText(string? text) => !string.IsNullOrWhiteSpace(text) && TransferText.IsMatch(text);

    /// <summary>The account suffix of a transfer description ("1436" in "Transfer to Checking *1436"), or empty.</summary>
    public static string TransferRef(string? text)
    {
        var m = text is null ? null : AccountRef.Match(text);
        return m is { Success: true } ? m.Groups[1].Value : string.Empty;
    }

    private static string GroupingName(FlowTxn t)
    {
        var norm = MerchantNormalizer.Normalize(t.Text);
        return IsTransferText(t.Text)
            ? $"{norm} {TransferMarker}{TransferRef(t.Text)}{TransferMarker}{t.Amount:0.##}"
            : norm;
    }

    /// <summary>Suppression/identity key for a grouping name: letters of the merchant, plus reference and amount for transfers.</summary>
    private static string FlowKey(string grouping)
    {
        var at = grouping.IndexOf(TransferMarker);
        if (at < 0)
        {
            return RecurringDetector.Key(grouping);
        }

        var head = RecurringDetector.Key(grouping[..at]);
        var parts = grouping[(at + 1)..].Split(TransferMarker);
        return $"{head}_{parts[0]}_{(parts.Length > 1 ? parts[1] : string.Empty)}_";
    }

    private static string StripMarker(string grouping)
    {
        var at = grouping.IndexOf(TransferMarker);
        return at < 0 ? grouping : grouping[..at].TrimEnd();
    }

    private static bool IsHabitMerchant(string normalized)
    {
        var padded = normalized + " ";
        return HabitWords.Any(w => padded.Contains(w, StringComparison.Ordinal));
    }

    // Words that mark a real bill, whose amount legitimately drifts month to month (utilities, insurance, loans).
    private static readonly string[] BillWords =
    [
        "ELECT", "PWR", "POWER", "GAS UTIL", "NATURAL GAS", "WATER", "UTIL", "SEWER", "INSURANCE", "ASSURANCE", "MORTGAGE", "LOAN",
        "STATE FARM", "PAYMENT", "AT&T", "ATT ", "VERIZON", "COX", "INTERNET", "MEDICAL", "MYCHART", "MY CHART",
    ];

    private static bool IsBillish(string normalized)
    {
        var padded = normalized + " ";
        return BillWords.Any(w => padded.Contains(w, StringComparison.Ordinal));
    }

    private static IReadOnlyList<DetectedFlow> Analyze(
        Guid accountId, bool isCredit, string key, List<FlowTxn> txns, DateOnly today, string normalizedName)
    {
        var flow = AnalyzeCore(accountId, isCredit, key, txns, today, normalizedName, out var extra);
        if (flow is null)
        {
            return [];
        }

        return extra is null ? [flow] : [flow, extra];
    }

    private static DetectedFlow? AnalyzeCore(
        Guid accountId, bool isCredit, string key, List<FlowTxn> txns, DateOnly today, string normalizedName, out DetectedFlow? extraFlow)
    {
        extraFlow = null;
        // One occurrence per date (a split deposit is still one paycheck); amounts on the same day are summed.
        var days = txns.GroupBy(t => t.Date).OrderBy(d => d.Key)
            .Select(d => (Date: d.Key, Amount: d.Sum(x => x.Amount))).ToList();
        if (days.Count < 3)
        {
            return null;
        }

        var gaps = new List<int>();
        for (var i = 1; i < days.Count; i++)
        {
            gaps.Add(days[i].Date.DayNumber - days[i - 1].Date.DayNumber);
        }

        var amounts = days.Select(d => d.Amount).ToList();
        var (median, isFixed, stableShare) = AmountStats(amounts);

        var last = days[^1].Date;
        var label = BestLabel(txns, StripMarker(normalizedName));
        if (normalizedName.Contains(TransferMarker))
        {
            label = $"{label} (${median:0.##})";
        }

        // A weekly/biweekly paycheck with one bigger check each month (a bonus week) is two flows: the regular check plus a
        // monthly extra for the difference. The base statistics then ignore the bonus checks so they don't look "variable".
        var split = SplitPeriodicExtra(days);
        var (baseMedian, baseFixed, baseStable) = split is null
            ? (median, isFixed, stableShare)
            : AmountStats(split.BaseAmounts);

        // Weekly (≥4 occurrences). Variable amounts only for income: weekly expense with varying amounts is shopping.
        var weekly = Share(gaps, 6, 8);
        if (weekly >= 0.7 && days.Count >= 4 && (baseFixed || baseStable >= 0.7 && isCredit))
        {
            var dow = ModeDayOfWeek(days.Select(d => d.Date));
            extraFlow = BuildExtra(split, key, accountId, isCredit, label, baseMedian, today);
            return Build(key, accountId, isCredit, label, baseMedian, baseFixed, "Weekly", null, null, null, dow,
                last, NextWeekly(last, 7, today), days.Count, weekly, expectedN: 6);
        }

        // Every two weeks (≥3).
        var biweekly = Share(gaps, 13, 15);
        if (biweekly >= 0.7 && (baseFixed || baseStable >= 0.7))
        {
            var dow = ModeDayOfWeek(days.Select(d => d.Date));
            extraFlow = BuildExtra(split, key, accountId, isCredit, label, baseMedian, today);
            return Build(key, accountId, isCredit, label, baseMedian, baseFixed, "EveryNWeeks", null, null, 2, dow,
                last, NextWeekly(last, 14, today), days.Count, biweekly, expectedN: 4);
        }

        // Every three weeks (≥3), e.g. a standing transfer that moves money out every third Friday.
        var triweekly = Share(gaps, 20, 22);
        if (triweekly >= 0.7 && (baseFixed || baseStable >= 0.7))
        {
            var dow = ModeDayOfWeek(days.Select(d => d.Date));
            return Build(key, accountId, isCredit, label, median, isFixed, "EveryNWeeks", null, null, 3, dow,
                last, NextWeekly(last, 21, today), days.Count, triweekly, expectedN: 3);
        }

        // Twice a month on two stable days (≥4 occurrences covering ≥2 months).
        if (TryTwiceMonthly(days.Select(d => d.Date).ToList(), out var d1, out var d2) && days.Count >= 4 && (isFixed || stableShare >= 0.7))
        {
            return Build(key, accountId, isCredit, label, median, isFixed, "TwiceMonthly", d1, d2, null, null,
                last, NextMonthly(last, today, d1, d2), days.Count, 0.95, expectedN: 4);
        }

        // Monthly: gaps of roughly a month, at most one per month, with a stable day and a stable amount.
        var monthly = Share(gaps, 26, 35);
        if (monthly >= (days.Count >= 4 ? 0.6 : 0.7))
        {
            // Billing day drifts across the month boundary (1st, 30th, 1st): measure day distance around the 30-day circle
            // and take the most common day as the anchor instead of a plain median.
            var monthDays = days.Select(d => d.Date.Day).ToList();
            var medDay = monthDays.OrderByDescending(r => monthDays.Count(x => DayDistance(x, r) <= 4)).ThenBy(r => r).First();
            var dayStable = monthDays.Count(x => DayDistance(x, medDay) <= 4) >= monthDays.Count * 0.7;
            // A bill shows up once a month; a merchant hit several times inside a month is a habit (fuel, lunch), not a bill.
            var crowded = gaps.Count(g => g < 20) > gaps.Count * 0.25;
            // Variable-amount expenses must be tightly clustered unless the merchant looks like a utility/insurer/lender;
            // a gas station visited around the same day each month varies far more than a power bill.
            var tight = amounts.Count(a => Math.Abs(a - median) <= median * 0.2m) / (double)amounts.Count;
            var amountOk = isCredit
                ? stableShare >= 0.6
                : isFixed || tight >= 0.6 || (IsBillish(normalizedName) && stableShare >= 0.6);
            if (dayStable && !crowded && amountOk)
            {
                return Build(key, accountId, isCredit, label, median, isFixed, "Monthly", Math.Min(medDay, 28), null, null, null,
                    last, NextMonthly(last, today, Math.Min(medDay, 28), null), days.Count, monthly, expectedN: 3);
            }
        }

        return null;
    }

    private static (decimal Median, bool IsFixed, double StableShare) AmountStats(List<decimal> amounts)
    {
        var median = Median(amounts);
        var fixedShare = amounts.Count(a => Math.Abs(a - median) <= median * 0.03m) / (double)amounts.Count;
        var stableShare = amounts.Count(a => Math.Abs(a - median) <= median * 0.35m) / (double)amounts.Count;
        return (median, fixedShare >= 0.8, stableShare);
    }

    private sealed record ExtraSplit(List<decimal> BaseAmounts, List<(DateOnly Date, decimal Amount)> Extras);

    /// <summary>
    /// Finds deposits clearly above the typical one that recur about once a month on a stable day (a bonus week). Needs at
    /// least two, no more than 40% of all deposits, with month-length gaps between them. Null when there is no such pattern.
    /// </summary>
    private static ExtraSplit? SplitPeriodicExtra(List<(DateOnly Date, decimal Amount)> days)
    {
        if (days.Count < 6)
        {
            return null;
        }

        var typical = Median(days.Select(d => d.Amount).ToList());
        var extras = days.Where(d => d.Amount > typical * ExtraRatio && d.Amount - typical >= 50m).ToList();
        if (extras.Count < 2 || extras.Count > days.Count * 0.4)
        {
            return null;
        }

        var gaps = new List<int>();
        for (var i = 1; i < extras.Count; i++)
        {
            gaps.Add(extras[i].Date.DayNumber - extras[i - 1].Date.DayNumber);
        }

        // Month-length gaps (4-5 weeks, allowing a holiday shift), and the same part of the month each time.
        var monthlyGaps = gaps.Count(g => g >= 24 && g <= 38) / (double)gaps.Count;
        var medDay = (int)Math.Round(Median(extras.Select(e => (decimal)e.Date.Day).ToList()));
        var dayStable = extras.Count(e => Math.Abs(e.Date.Day - medDay) <= 5) >= extras.Count * 0.7;
        if (monthlyGaps < 0.7 || !dayStable)
        {
            return null;
        }

        var extraDates = extras.Select(e => e.Date).ToHashSet();
        var baseAmounts = days.Where(d => !extraDates.Contains(d.Date)).Select(d => d.Amount).ToList();
        return baseAmounts.Count >= 3 ? new ExtraSplit(baseAmounts, extras) : null;
    }

    /// <summary>The monthly flow for the bonus: average bonus check minus the regular check, on the usual day of the month.</summary>
    private static DetectedFlow? BuildExtra(
        ExtraSplit? split, string key, Guid accountId, bool isCredit, string label, decimal baseAmount, DateOnly today)
    {
        if (split is null)
        {
            return null;
        }

        var diffs = split.Extras.Select(e => e.Amount - baseAmount).ToList();
        var mean = diffs.Average();
        if (mean <= 0)
        {
            return null;
        }

        var isFixed = diffs.All(d => Math.Abs(d - mean) <= mean * 0.1m);
        var day = Math.Min((int)Math.Round(Median(split.Extras.Select(e => (decimal)e.Date.Day).ToList())), 28);
        var lastExtra = split.Extras[^1].Date;
        var gaps = new List<int>();
        for (var i = 1; i < split.Extras.Count; i++)
        {
            gaps.Add(split.Extras[i].Date.DayNumber - split.Extras[i - 1].Date.DayNumber);
        }

        var built = Build(key + "|extra", accountId, isCredit, label + ExtraSuffix, mean, isFixed, "Monthly", day, null, null, null,
            lastExtra, NextMonthly(lastExtra, today, day, null), split.Extras.Count, Share(gaps, 24, 38), expectedN: 3);
        return built is null ? null : built with { IsPeriodicExtra = true };
    }

    private static DetectedFlow? Build(
        string key, Guid accountId, bool isCredit, string label, decimal amount, bool isFixed, string frequency,
        int? day, int? day2, int? weeks, string? dow, DateOnly last, DateOnly next, int count, double consistency, int expectedN)
    {
        var amountScore = isFixed ? 1.0 : 0.8;
        var countScore = Math.Min(1.0, count / (double)expectedN);
        var confidence = Math.Round(0.5 * consistency + 0.3 * amountScore + 0.2 * countScore, 2);
        return new DetectedFlow(
            Key: $"{accountId:N}|{(isCredit ? "C" : "D")}|{key}",
            AccountId: accountId,
            Name: label,
            Direction: isCredit ? "Credit" : "Debit",
            Amount: Math.Round(amount, 2),
            AmountModel: isFixed ? "Fixed" : "Estimated",
            Frequency: frequency,
            DayOfMonth: day,
            SecondDayOfMonth: day2,
            IntervalWeeks: weeks,
            AnchorDayOfWeek: dow,
            LastSeen: last,
            NextExpected: next,
            Occurrences: count,
            Confidence: confidence);
    }

    private static int DayDistance(int a, int b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 30 - d);
    }

    private static int PeriodDays(DetectedFlow flow) =>
        flow.Frequency switch { "Weekly" => 7, "EveryNWeeks" => 7 * (flow.IntervalWeeks ?? 2), "TwiceMonthly" => 16, _ => 31 };

    private static double Share(List<int> gaps, int min, int max) =>
        gaps.Count(g => g >= min && g <= max) / (double)gaps.Count;

    private static string ModeDayOfWeek(IEnumerable<DateOnly> dates) =>
        dates.GroupBy(d => d.DayOfWeek).OrderByDescending(g => g.Count()).First().Key.ToString();

    private static DateOnly NextWeekly(DateOnly last, int stepDays, DateOnly today)
    {
        var next = last.AddDays(stepDays);
        while (next < today)
        {
            next = next.AddDays(stepDays);
        }

        return next;
    }

    private static DateOnly NextMonthly(DateOnly last, DateOnly today, int day, int? day2)
    {
        var candidates = new List<DateOnly>();
        var cursor = new DateOnly(last.Year, last.Month, 1);
        for (var i = 0; i < 6; i++)
        {
            var month = cursor.AddMonths(i);
            foreach (var d in new[] { day, day2 ?? day }.Distinct())
            {
                var dom = Math.Min(d, DateTime.DaysInMonth(month.Year, month.Month));
                var date = new DateOnly(month.Year, month.Month, dom);
                if (date > last && date >= today)
                {
                    candidates.Add(date);
                }
            }
        }

        return candidates.OrderBy(c => c).FirstOrDefault(last.AddMonths(1));
    }

    private static bool TryTwiceMonthly(List<DateOnly> dates, out int d1, out int d2)
    {
        d1 = d2 = 0;
        var byMonth = dates.GroupBy(d => (d.Year, d.Month)).ToList();
        if (byMonth.Count < 2)
        {
            return false;
        }

        // Interior months must each have exactly two occurrences (the first/last month may be partial).
        var interior = byMonth.Skip(1).Take(Math.Max(0, byMonth.Count - 2)).ToList();
        if (interior.Any(m => m.Count() != 2) || byMonth.Any(m => m.Count() > 2))
        {
            return false;
        }

        var firsts = byMonth.Where(m => m.Count() == 2).Select(m => m.Min(d => d.Day)).ToList();
        var seconds = byMonth.Where(m => m.Count() == 2).Select(m => m.Max(d => d.Day)).ToList();
        if (firsts.Count == 0)
        {
            return false;
        }

        var first = (int)Median(firsts.Select(x => (decimal)x).ToList());
        var second = (int)Median(seconds.Select(x => (decimal)x).ToList());
        d1 = first;
        d2 = second;
        return second - first >= 10
            && firsts.All(x => Math.Abs(x - first) <= 2)
            && seconds.All(x => Math.Abs(x - second) <= 2);
    }

    /// <summary>
    /// Display name: an alias the user gave it (mixed case) as-is, otherwise the normalized merchant, which drops the
    /// processor prefix, reference numbers and state.
    /// </summary>
    private static string BestLabel(List<FlowTxn> txns, string normalizedName)
    {
        var raw = txns.GroupBy(t => t.Text.Trim()).OrderByDescending(g => g.Count()).First().Key;
        var text = raw.Any(char.IsLower) ? raw : normalizedName;
        var collapsed = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 80 ? collapsed[..80] : collapsed;
    }

    private static decimal Median(List<decimal> values)
    {
        var s = values.OrderBy(x => x).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }
}
