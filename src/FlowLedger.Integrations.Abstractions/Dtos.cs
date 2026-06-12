using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Integrations.Abstractions;

// ── Member / Connection ───────────────────────────────────────────────────────

/// <summary>
/// Provider-issued reference that identifies a linked member (institution + user pair).
/// Opaque to the domain — stored verbatim for use in subsequent provider calls.
/// </summary>
public sealed record ProviderMemberRef(
    /// <summary>Provider's own identifier for this member.</summary>
    string ProviderId,

    /// <summary>Human-readable institution name returned by the provider.</summary>
    string InstitutionName,

    /// <summary>Current connection lifecycle state.</summary>
    ConnectionStatus Status);

// ── Account ───────────────────────────────────────────────────────────────────

/// <summary>
/// Provider-neutral account descriptor returned after a sync.
/// Domain layer maps this to its own Account aggregate; it must never hold SDK types.
/// </summary>
public sealed record ProviderAccount(
    /// <summary>Provider's stable identifier for this account.</summary>
    string ProviderId,

    /// <summary>Display name of the account (e.g. "Chase Checking ••4321").</summary>
    string Name,

    /// <summary>Account type string as returned by the provider (e.g. "CHECKING").</summary>
    string AccountType,

    /// <summary>Current ledger balance (may differ from available balance).</summary>
    Money Balance,

    /// <summary>Available balance if the provider returns it; otherwise null.</summary>
    Money? AvailableBalance,

    /// <summary>ISO 4217 currency code as reported by the provider.</summary>
    string CurrencyCode);

// ── Transaction ───────────────────────────────────────────────────────────────

/// <summary>
/// Provider-neutral transaction descriptor. Contains all raw inputs needed to build
/// a <see cref="FlowLedger.Domain.ValueObjects.TransactionFingerprint"/> and normalise
/// the transaction into a domain <c>Transaction</c> aggregate.
/// </summary>
public sealed record ProviderTransaction(
    /// <summary>Provider's stable identifier for this transaction (null when not available).</summary>
    string? ProviderId,

    /// <summary>Provider's identifier for the account this transaction belongs to.</summary>
    string ProviderAccountId,

    /// <summary>Date the transaction was posted (settled). Use this for fingerprinting.</summary>
    DateOnly PostedDate,

    /// <summary>True when the transaction is still pending (not yet settled).</summary>
    bool IsPending,

    /// <summary>
    /// Signed amount: negative = debit/outflow, positive = credit/inflow.
    /// Uses <see cref="Money"/> to guarantee exact decimal arithmetic.
    /// </summary>
    Money Amount,

    /// <summary>Raw description text as returned by the provider.</summary>
    string RawDescription,

    /// <summary>Normalised merchant name if the provider supplies it; otherwise null.</summary>
    string? MerchantName,

    /// <summary>Provider-supplied category label; may be null.</summary>
    string? ProviderCategory);

// ── Pagination / Cursor ───────────────────────────────────────────────────────

/// <summary>
/// Opaque bookmark that tracks the high-water mark for incremental transaction syncing.
/// The provider implementation owns the encoding; callers must treat it as opaque.
/// </summary>
public sealed record SyncCursor(
    /// <summary>Serialised cursor value produced by the provider.</summary>
    string Value)
{
    /// <summary>Sentinel that means "start from the beginning".</summary>
    public static readonly SyncCursor Initial = new(string.Empty);

    /// <summary>True when this cursor represents "no previous position" (initial load).</summary>
    public bool IsInitial => string.IsNullOrEmpty(Value);
}

/// <summary>
/// A single page of transactions returned by an incremental or full fetch.
/// </summary>
public sealed record TransactionPage(
    /// <summary>Transactions in this page, ordered oldest-first.</summary>
    IReadOnlyList<ProviderTransaction> Items,

    /// <summary>
    /// Cursor to pass on the next call. When <see cref="HasMore"/> is false, this cursor
    /// still represents the latest position and should be persisted for future incremental syncs.
    /// </summary>
    SyncCursor NextCursor,

    /// <summary>True when additional pages are available.</summary>
    bool HasMore)
{
    /// <summary>Returns an empty page with the given cursor (e.g. "already up to date").</summary>
    public static TransactionPage Empty(SyncCursor cursor) =>
        new(Array.Empty<ProviderTransaction>(), cursor, false);
}

// ── Webhook ───────────────────────────────────────────────────────────────────

/// <summary>Structured event emitted by the provider via a webhook callback.</summary>
public sealed record ProviderWebhookEvent(
    /// <summary>Provider-specific event type token (e.g. "SYNC_COMPLETE").</summary>
    string EventType,

    /// <summary>Provider member id the event concerns.</summary>
    string MemberId,

    /// <summary>ISO 8601 timestamp when the event was emitted.</summary>
    DateTimeOffset OccurredAt,

    /// <summary>Additional arbitrary metadata from the payload; may be empty.</summary>
    IReadOnlyDictionary<string, string> Metadata);
