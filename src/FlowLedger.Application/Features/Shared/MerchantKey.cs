namespace FlowLedger.Application.Features.Shared;

/// <summary>
/// Coarse merchant grouping key shared by recurring-flow detection, recurring suggestions and the bill sheet:
/// letters only, lower-cased (invariant), so "ATT*BILL 1234" and "ATT*BILL 5678" are one merchant.
/// Deliberately distinct from <c>MerchantNormalizer.Key</c> and <c>SheetMatcher.Normalize</c>, which have other rules.
/// </summary>
internal static class MerchantKey
{
    public static string From(string text)
        => new(text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
}
