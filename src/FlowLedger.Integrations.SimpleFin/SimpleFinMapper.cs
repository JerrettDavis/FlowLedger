using System.Globalization;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.SimpleFin;

internal static class SimpleFinMapper
{
    public static string AccountId(SfAccount a) =>
        $"{a.Org?.Id ?? a.Org?.Domain ?? "org"}:{a.Id ?? throw new FatalProviderException("SimpleFIN account is missing its id.")}";

    /// <summary>SimpleFIN has no account type; infer a canonical token from the institution + account name.</summary>
    public static string InferType(SfAccount a)
    {
        var s = $"{a.Org?.Name} {a.Name}".ToUpperInvariant();
        if (s.Contains("MORTGAGE")) { return "MORTGAGE"; }
        if (s.Contains("LOAN") || s.Contains("STUDENT") || s.Contains("AUTO FIN") || s.Contains("INSTALLMENT")) { return "LOAN"; }
        if (s.Contains("CREDIT") || s.Contains("CARD") || s.Contains("VISA") || s.Contains("MASTERCARD")
            || s.Contains("AMEX") || s.Contains("AMERICAN EXPRESS") || s.Contains("DISCOVER")) { return "CREDIT_CARD"; }
        if (s.Contains("SAVING") || s.Contains("MONEY MARKET")) { return "SAVINGS"; }
        if (s.Contains("401") || s.Contains("IRA") || s.Contains("BROKERAGE") || s.Contains("INVEST")) { return "INVESTMENT"; }
        return "CHECKING";
    }

    public static ProviderAccount ToAccount(SfAccount a)
    {
        var currency = NormalizeCurrency(a.Currency);
        var type = InferType(a);
        var balance = ParseDecimal(a.Balance) ?? 0m;
        var available = ParseDecimal(a.AvailableBalance);

        // SimpleFIN reports what you HAVE (liabilities negative). FlowLedger (like MX) stores what you OWE as positive.
        if (type is "CREDIT_CARD" or "LOAN" or "MORTGAGE")
        {
            balance = Math.Abs(balance);
            available = available is { } av ? Math.Abs(av) : null;
        }

        var org = string.IsNullOrWhiteSpace(a.Org?.Name) ? a.Org?.Domain : a.Org!.Name;
        var name = string.IsNullOrWhiteSpace(org) ? (a.Name ?? "SimpleFIN Account") : $"{org} - {a.Name}";
        return new ProviderAccount(
            AccountId(a), name, type,
            new Money(balance, new Currency(currency)),
            available is { } v ? new Money(v, new Currency(currency)) : null,
            currency);
    }

    public static ProviderTransaction ToTransaction(SfAccount a, SfTransaction t)
    {
        var currency = NormalizeCurrency(a.Currency);
        var pending = t.Pending == true || t.Posted <= 0;
        var epoch = t.Posted > 0 ? t.Posted : (t.TransactedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime);
        var desc = string.IsNullOrWhiteSpace(t.Description) ? "UNKNOWN" : t.Description.Trim();
        return new ProviderTransaction(
            t.Id, AccountId(a), date, pending,
            new Money(ParseDecimal(t.Amount) ?? 0m, new Currency(currency)),
            desc, null, null);
    }

    private static decimal? ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;

    // SimpleFIN currency may be an ISO code or a custom-currency URL; anything unusual falls back to USD.
    private static string NormalizeCurrency(string? c) =>
        c is { Length: 3 } && c.All(char.IsLetter) ? c.ToUpperInvariant() : "USD";
}
