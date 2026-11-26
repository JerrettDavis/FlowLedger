using System.Globalization;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Quiltt;

internal static class QuilttMapper
{
    public static ProviderAccount ToAccount(QAccount a)
    {
        var id = a.Id ?? throw new FatalProviderException("Quiltt account is missing its id.");
        var currency = Currency(a.CurrencyCode);
        var type = MapType(a.Kind, a.Name);
        var balance = a.Balance?.Current ?? 0m;
        var available = a.Balance?.Available;

        // Quiltt reports what you HAVE (liabilities negative). FlowLedger stores what you OWE as positive.
        if (type is "CREDIT_CARD" or "LOAN" or "MORTGAGE")
        {
            balance = -balance;
            available = available is { } av ? -av : null;
        }

        var inst = a.Institution?.Name;
        var baseName = string.IsNullOrWhiteSpace(a.Name) ? "Quiltt Account" : a.Name!.Trim();
        var mask = string.IsNullOrWhiteSpace(a.Mask) ? string.Empty : $" ••{a.Mask}";
        var name = string.IsNullOrWhiteSpace(inst) ? baseName + mask : $"{inst} - {baseName}{mask}";

        return new ProviderAccount(
            id, name, type,
            new Money(balance, new Currency(currency)),
            available is { } v ? new Money(v, new Currency(currency)) : null,
            currency);
    }

    /// <summary>True when the row is a real ledger entry: not PROJECTED and carrying a parseable date.</summary>
    public static bool IsIngestible(QTransaction t) =>
        !string.Equals(t.Status, "PROJECTED", StringComparison.OrdinalIgnoreCase)
        && DateOnly.TryParse(t.Date, CultureInfo.InvariantCulture, out _);

    public static ProviderTransaction ToTransaction(QTransaction t, string fallbackCurrency)
    {
        var accountId = t.Account?.Id ?? throw new FatalProviderException("Quiltt transaction is missing its account.");
        var date = DateOnly.TryParse(t.Date, CultureInfo.InvariantCulture, out var d)
            ? d
            : DateOnly.FromDateTime(DateTime.UtcNow);

        // entryType is authoritative when present; otherwise trust the sign (negative = outflow).
        var raw = t.Amount ?? 0m;
        var amount = t.EntryType?.ToUpperInvariant() switch
        {
            "DEBIT" => -Math.Abs(raw),
            "CREDIT" => Math.Abs(raw),
            _ => raw,
        };

        var currency = string.IsNullOrWhiteSpace(t.CurrencyCode) ? fallbackCurrency : Currency(t.CurrencyCode);
        var pending = string.Equals(t.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(t.Status, "PROJECTED", StringComparison.OrdinalIgnoreCase);
        var desc = string.IsNullOrWhiteSpace(t.Description) ? "UNKNOWN" : t.Description.Trim();

        return new ProviderTransaction(t.Id, accountId, date, pending, new Money(amount, new Currency(currency)), desc, null, null);
    }

    /// <summary>Quiltt AccountKind is coarse (DEPOSITORY, INVESTMENT, CREDIT, LOAN, INSURANCE; extensible); the name refines it.</summary>
    internal static string MapType(string? type, string? name)
    {
        var t = (type ?? string.Empty).ToUpperInvariant();
        var n = (name ?? string.Empty).ToUpperInvariant();
        if (t.Contains("MORTGAGE") || n.Contains("MORTGAGE")) { return "MORTGAGE"; }
        if (t.Contains("LOAN") || t.Contains("LINE_OF_CREDIT")) { return "LOAN"; }
        if (t.Contains("CREDIT") || t.Contains("CARD")) { return "CREDIT_CARD"; }
        if (t.Contains("SAVING") || t.Contains("MONEY_MARKET") || (t == "DEPOSITORY" && (n.Contains("SAVING") || n.Contains("MONEY MARKET")))) { return "SAVINGS"; }
        if (t.Contains("INVEST") || t.Contains("BROKERAGE") || t.Contains("RETIREMENT") || t.Contains("401")) { return "INVESTMENT"; }
        return "CHECKING";
    }

    internal static string Currency(string? c) =>
        c is { Length: 3 } && c.All(char.IsLetter) ? c.ToUpperInvariant() : "USD";
}
