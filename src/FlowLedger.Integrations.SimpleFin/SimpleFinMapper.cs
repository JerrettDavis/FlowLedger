using System.Globalization;
using System.Text.RegularExpressions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.SimpleFin;

internal static class SimpleFinMapper
{
    public static string AccountId(SfAccount a) =>
        $"{a.Org?.Id ?? a.Org?.Domain ?? "org"}:{a.Id ?? throw new FatalProviderException("SimpleFIN account is missing its id.")}";

    /// <summary>SimpleFIN has no account type; infer a canonical token from the institution + account name.</summary>
    // Word-boundary matches so short tokens (IRA, SPS) cannot hit inside other words (e.g. "Miramar").
    private static readonly Regex MortgageRx = new(@"\bMORTGAGE\b|\bHELOC\b|\bHOME EQUITY\b", RegexOptions.Compiled);
    private static readonly Regex LoanRx = new(@"\bLOAN\b|\bSTUDENT\b|\bAUTO FIN|\bINSTALLMENT\b", RegexOptions.Compiled);
    // Generic card words may appear in the institution OR account name ("Arvest Bank - Personal Credit Card").
    private static readonly Regex CardGenericRx = new(@"\bCREDIT\b(?! UNION)|\bCARD\b", RegexOptions.Compiled);
    // Network/product brand words are only trusted in the ACCOUNT name: they are also bank names ("Discover Bank").
    private static readonly Regex CardBrandRx = new(
        @"\bVISA\b|\bMASTERCARD\b|\bAMEX\b|\bAMERICAN EXPRESS\b|\bDISCOVER\b|\bFREEDOM\b|\bSAPPHIRE\b|\bSLATE\b|\bCUSTOM CASH\b|\bDOUBLE CASH\b|\bQUICKSILVER\b",
        RegexOptions.Compiled);
    private static readonly Regex CheckingRx = new(@"\bCHECKING\b", RegexOptions.Compiled);
    private static readonly Regex SavingsRx = new(@"\bSAVINGS?\b|\bMONEY MARKET\b", RegexOptions.Compiled);
    private static readonly Regex InvestmentRx = new(
        @"\b401\(?K\)?\b|\bIRA\b|\bROTH\b|\bBROKERAGE\b|\bINVEST|\bAMERIPRISE\b|\bWEALTH\b|\bSPS\b|\bADVISOR\b|\bVANGUARD\b|\bFIDELITY\b",
        RegexOptions.Compiled);

    public static string InferType(SfAccount a)
    {
        var name = (a.Name ?? string.Empty).ToUpperInvariant();
        var full = $"{a.Org?.Name} {name}".ToUpperInvariant();

        if (MortgageRx.IsMatch(full)) { return "MORTGAGE"; }
        if (LoanRx.IsMatch(full)) { return "LOAN"; }
        // An explicit product word in the account name beats any brand heuristic ("Rewards Checking", "Discover Savings").
        if (SavingsRx.IsMatch(name)) { return "SAVINGS"; }
        if (CheckingRx.IsMatch(name)) { return "CHECKING"; }
        if (CardGenericRx.IsMatch(full) || CardBrandRx.IsMatch(name)) { return "CREDIT_CARD"; }
        if (InvestmentRx.IsMatch(full)) { return "INVESTMENT"; }
        if (SavingsRx.IsMatch(full)) { return "SAVINGS"; }
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
