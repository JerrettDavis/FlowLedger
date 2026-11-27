using System.Text.RegularExpressions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>
/// Deterministic, side-effect-free tag rules for newly ingested transactions. Tags are lower-case kebab labels.
/// Only tags in <see cref="Vocabulary"/> are ever managed automatically; anything else a user adds is left alone.
/// </summary>
internal static class AutoTagger
{
    public const decimal LargeThreshold = 500m;

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Tag, Regex Pattern, string? Direction)[] Rules =
    [
        ("refund", Rx(@"refund|\breturn\b|reversal|chargeback|credit adj"), nameof(TransactionDirection.Credit)),
        ("interest", Rx(@"interest"), nameof(TransactionDirection.Credit)),
        ("payroll", Rx(@"payroll|direct dep|salary|paycheck"), nameof(TransactionDirection.Credit)),
        ("fee", Rx(@"\bfee\b|service charge|overdraft|maintenance fee|finance charge"), null),
        ("atm", Rx(@"\batm\b|cash withdrawal"), null),
        ("subscription", Rx(@"subscription|netflix|spotify|hulu|disney\+|patreon|prime video|anthropic|openai|github|adobe|jetbrains|digitalocean|icloud|google (one|storage|workspace)"), nameof(TransactionDirection.Debit)),
        ("online", Rx(@"\.com\b|\.net\b|\.org\b|\.io\b|www\.|paypal|amzn|amazon|\bsq \*|\btst\*|\bpp\*"), null),
        ("international", Rx(@"foreign|intl|international|\bfx\b|currency conversion|\b(sgp|gbr|can|mex|irl|deu|fra|jpn|aus)\b"), null),
        ("peer-payment", Rx(@"venmo|zelle|cash app"), null),
        ("food-delivery", Rx(@"doordash|grubhub|uber ?eats|postmates|instacart"), null),
        ("loan-payment", Rx(@"loan|mortgage|\bhmf\b|auto fin|principal"), nameof(TransactionDirection.Debit)),
    ];

    /// <summary>Every tag this class can assign (including the amount/status based ones).</summary>
    public static readonly IReadOnlySet<string> Vocabulary =
        new HashSet<string>(Rules.Select(r => r.Tag).Concat(["large", "pending", "needs-review"]), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Suggest(string? merchant, string? description, string direction, decimal amount, TransactionStatus status)
    {
        var text = string.Join(' ', new[] { merchant, description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var tags = new List<string>();
        foreach (var (tag, pattern, dir) in Rules)
        {
            if (dir is not null && !string.Equals(dir, direction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text.Length > 0 && pattern.IsMatch(text))
            {
                tags.Add(tag);
            }
        }

        if (string.Equals(direction, nameof(TransactionDirection.Debit), StringComparison.OrdinalIgnoreCase) && amount >= LargeThreshold)
        {
            tags.Add("large");
        }

        if (status == TransactionStatus.Pending)
        {
            tags.Add("pending");
        }

        if (status == TransactionStatus.NeedsReview)
        {
            tags.Add("needs-review");
        }

        return tags;
    }
}
