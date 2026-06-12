using System.Globalization;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using PatternKit.Behavioral.Strategy;

namespace FlowLedger.Integrations.Mx.Mapping;

/// <summary>
/// Pure, deterministic mapping functions: MX wire record → provider-neutral DTO.
/// No I/O, no clock, no randomness — identical input always yields identical output.
/// This is the single place that knows how MX field semantics map to the contract.
///
/// Uses PatternKit.Core's <see cref="Strategy{TIn,TOut}"/> for the two string-dispatch
/// mappings (<see cref="ToConnectionStatus"/> and <see cref="NormalizeAccountType"/>) so
/// each rule lives in one declaration and is independently readable. Strategy instances are
/// built once at class-load time (immutable, compiled artifacts).
/// </summary>
internal static class MxMapper
{
    private const string DefaultCurrency = "USD";

    // ── Connection-status mapping (PatternKit Strategy) ───────────────────────────
    //
    // Maps a normalised (trimmed, uppercased) MX connection_status string to the
    // contract's ConnectionStatus enum. First-match semantics — identical to the
    // original switch expression's fall-through order.

    private static readonly Strategy<string, ConnectionStatus> _connectionStatusStrategy =
        Strategy<string, ConnectionStatus>.Create()

            .When(static (in string s) => s is "CONNECTED" or "RECONNECTED" or "UPDATED" or "RESUMED")
            .Then(static (in string _) => ConnectionStatus.Healthy)

            .When(static (in string s) => s is "CREATED" or "IMPORTED" or "PENDING")
            .Then(static (in string _) => ConnectionStatus.ConnectionPending)

            .When(static (in string s) => s == "DELAYED")
            .Then(static (in string _) => ConnectionStatus.Syncing)

            .When(static (in string s) => s == "DEGRADED")
            .Then(static (in string _) => ConnectionStatus.Degraded)

            .When(static (in string s) => s is "CHALLENGED" or "DENIED" or "REJECTED" or "LOCKED"
                                              or "IMPEDED" or "PREVENTED" or "EXPIRED" or "IMPAIRED")
            .Then(static (in string _) => ConnectionStatus.NeedsUserAction)

            .When(static (in string s) => s == "DISABLED")
            .Then(static (in string _) => ConnectionStatus.Disabled)

            .When(static (in string s) => s is "DISCONNECTED" or "DISCONTINUED" or "CLOSED" or "FAILED")
            .Then(static (in string _) => ConnectionStatus.Error)

            // Unknown status → treat as pending (safe default).
            .Default(static (in string _) => ConnectionStatus.ConnectionPending)

            .Build();

    // ── Account-type mapping (PatternKit Strategy) ────────────────────────────────
    //
    // Maps a normalised MX account type string to a canonical, domain-aligned token.
    // The domain's MapProviderAccountType switch consumes these uppercase tokens.
    // Single source of truth for MX → canonical account-type mapping (DRY).

    private static readonly Strategy<string, string> _accountTypeStrategy =
        Strategy<string, string>.Create()

            .When(static (in string s) => s == "CHECKING").Then(static (in string _) => "CHECKING")
            .When(static (in string s) => s == "SAVINGS").Then(static (in string _) => "SAVINGS")
            .When(static (in string s) => s is "CREDIT_CARD" or "CREDITCARD" or "LINE_OF_CREDIT")
                .Then(static (in string _) => "CREDIT_CARD")
            .When(static (in string s) => s == "LOAN").Then(static (in string _) => "LOAN")
            .When(static (in string s) => s == "MORTGAGE").Then(static (in string _) => "MORTGAGE")
            .When(static (in string s) => s is "INVESTMENT" or "BROKERAGE")
                .Then(static (in string _) => "INVESTMENT")
            .When(static (in string s) => s is "PREPAID" or "CASH").Then(static (in string _) => "CASH")

            // Safe default mirrors the domain's fallback.
            .Default(static (in string _) => "CHECKING")

            .Build();

    // ── Accounts ──────────────────────────────────────────────────────────────

    public static ProviderAccount ToProviderAccount(MxAccount a)
    {
        ArgumentNullException.ThrowIfNull(a);

        var currency = NormalizeCurrency(a.CurrencyCode);
        var balance = new Money(a.Balance ?? 0m, new Currency(currency));
        Money? available = a.AvailableBalance is { } av
            ? new Money(av, new Currency(currency))
            : null;

        return new ProviderAccount(
            ProviderId: a.Guid ?? throw new FatalProviderException("MX account is missing its guid."),
            Name: string.IsNullOrWhiteSpace(a.Name) ? "MX Account" : a.Name,
            AccountType: NormalizeAccountType(a.Type),
            Balance: balance,
            AvailableBalance: available,
            CurrencyCode: currency);
    }

    /// <summary>
    /// Maps an MX account <c>type</c> string to a canonical, domain-aligned account-type token.
    /// The domain's MapProviderAccountType switch consumes these uppercase tokens.
    /// Single source of truth for MX → canonical account-type mapping (DRY).
    /// </summary>
    public static string NormalizeAccountType(string? mxType)
    {
        var key = (mxType ?? string.Empty).Trim().ToUpperInvariant();
        return _accountTypeStrategy.Execute(in key);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public static ProviderTransaction ToProviderTransaction(MxTransaction t)
    {
        ArgumentNullException.ThrowIfNull(t);

        var currency = NormalizeCurrency(t.CurrencyCode);
        var postedDate = ParsePostedDate(t);
        var isPending = string.Equals(t.Status, "PENDING", StringComparison.OrdinalIgnoreCase);

        // MX `amount` is unsigned; `type` carries the direction (DEBIT/CREDIT).
        // The contract expects a signed amount: negative = outflow/debit, positive = inflow/credit.
        var magnitude = Math.Abs(t.Amount ?? 0m);
        var signed = string.Equals(t.Type, "DEBIT", StringComparison.OrdinalIgnoreCase)
            ? -magnitude
            : magnitude;

        var rawDescription = FirstNonEmpty(t.OriginalDescription, t.Description) ?? "UNKNOWN";
        var merchant = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description;

        return new ProviderTransaction(
            ProviderId: t.Guid,
            ProviderAccountId: t.AccountGuid
                ?? throw new FatalProviderException("MX transaction is missing account_guid."),
            PostedDate: postedDate,
            IsPending: isPending,
            Amount: new Money(signed, new Currency(currency)),
            RawDescription: rawDescription,
            MerchantName: merchant,
            ProviderCategory: t.Category);
    }

    // ── Connection status ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps an MX member <c>connection_status</c> string to the contract's
    /// <see cref="ConnectionStatus"/>. Single source of truth (DRY).
    /// </summary>
    public static ConnectionStatus ToConnectionStatus(string? mxConnectionStatus)
    {
        var key = (mxConnectionStatus ?? string.Empty).Trim().ToUpperInvariant();
        return _connectionStatusStrategy.Execute(in key);
    }

    /// <summary>
    /// True when an MX connection_status requires the end-user to act
    /// (re-auth, MFA, re-link). Used to translate live calls into
    /// <see cref="NeedsUserActionProviderException"/>.
    /// </summary>
    public static bool RequiresUserAction(string? mxConnectionStatus) =>
        ToConnectionStatus(mxConnectionStatus) == ConnectionStatus.NeedsUserAction;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DateOnly ParsePostedDate(MxTransaction t)
    {
        // Prefer the `date` field (YYYY-MM-DD); fall back to posted_at / transacted_at datetimes.
        if (TryParseDateOnly(t.Date, out var d))
        {
            return d;
        }

        if (TryParseDateTimeAsDate(t.PostedAt, out d))
        {
            return d;
        }

        if (TryParseDateTimeAsDate(t.TransactedAt, out d))
        {
            return d;
        }

        return DateOnly.MinValue;
    }

    private static bool TryParseDateOnly(string? value, out DateOnly result)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseDateTimeAsDate(string? value, out DateOnly result)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            result = DateOnly.FromDateTime(dto.UtcDateTime);
            return true;
        }

        result = default;
        return false;
    }

    private static string NormalizeCurrency(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultCurrency;
        }

        var trimmed = code.Trim().ToUpperInvariant();
        return trimmed.Length == 3 && trimmed.All(char.IsLetter) ? trimmed : DefaultCurrency;
    }

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
        {
            return a;
        }

        return string.IsNullOrWhiteSpace(b) ? null : b;
    }
}
