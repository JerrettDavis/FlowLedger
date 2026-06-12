using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// The single seam between FlowLedger's domain/application layer and any financial
/// data aggregation provider (MX, Plaid, CSV, Simulated, …).
///
/// Lifecycle segments:
///   1. Connection — begin a Connect flow, poll or retrieve status.
///   2. Accounts   — fetch the account list for a connected member.
///   3. Transactions — incremental cursor-based fetch with pagination.
///   4. Webhooks   — verify inbound signatures and parse structured events.
///
/// Thread safety: implementations are NOT required to be thread-safe.
/// Callers should resolve a fresh instance per operation scope.
/// </summary>
public interface IFinancialDataProvider
{
    // ── Metadata ──────────────────────────────────────────────────────────────

    /// <summary>Human-readable name of this provider (e.g. "MX", "Simulated").</summary>
    string ProviderName { get; }

    /// <summary>Capabilities advertised by this provider. Check before calling optional methods.</summary>
    ProviderCapabilities Capabilities { get; }

    // ── Connection lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Initiates a new member connection.  For OAuth-based providers this may
    /// return a URL or widget token the UI must present to the user.
    /// Returns the initial <see cref="ProviderMemberRef"/> (status = ConnectionPending).
    /// </summary>
    /// <param name="tenantId">Tenant owning this connection; used for isolation.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<ProviderMemberRef> BeginConnectionAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current connection status for a previously created member.
    /// May make a live provider call; callers should cache the result appropriately.
    /// </summary>
    /// <param name="memberProviderId">
    ///   The <see cref="ProviderMemberRef.ProviderId"/> returned by <see cref="BeginConnectionAsync"/>.
    /// </param>
    Task<ConnectionStatus> GetConnectionStatusAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default);

    // ── Accounts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all accounts associated with the given member.
    /// Throws <see cref="NeedsUserActionProviderException"/> when the connection needs repair.
    /// </summary>
    Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default);

    // ── Transactions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns one page of transactions for the given provider account,
    /// starting from <paramref name="cursor"/> (pass <see cref="SyncCursor.Initial"/> for
    /// the first call or a full-history load).
    ///
    /// Callers must iterate until <see cref="TransactionPage.HasMore"/> is false,
    /// persisting each <see cref="TransactionPage.NextCursor"/> for future incremental syncs.
    /// </summary>
    /// <param name="providerAccountId">Provider's account identifier.</param>
    /// <param name="cursor">
    ///   Opaque position bookmark from a previous call; use <see cref="SyncCursor.Initial"/>
    ///   to start from the beginning.
    /// </param>
    /// <param name="pageSize">Requested page size; providers may return fewer items.</param>
    Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId,
        SyncCursor cursor,
        int pageSize = 100,
        CancellationToken cancellationToken = default);

    // ── Webhooks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the HMAC signature on an inbound webhook request.
    /// Throws <see cref="InvalidWebhookSignatureException"/> when verification fails.
    /// The caller should return HTTP 401/403 and not process the payload.
    /// </summary>
    /// <param name="rawBody">Raw request body bytes.</param>
    /// <param name="signatureHeader">Value of the provider's signature header.</param>
    Task VerifyWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        string signatureHeader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a previously verified webhook payload into a structured event.
    /// Must only be called AFTER <see cref="VerifyWebhookAsync"/> succeeds.
    /// </summary>
    /// <param name="rawBody">Raw request body bytes.</param>
    Task<ProviderWebhookEvent> ParseWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        CancellationToken cancellationToken = default);
}
