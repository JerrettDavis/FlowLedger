using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using FlowLedger.Integrations.Mx.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Real MX (mx.com) implementation of <see cref="IFinancialDataProvider"/>.
///
/// Delegates all wire concerns to <see cref="MxApiClient"/> and all shape translation to
/// <see cref="MxMapper"/> / <see cref="MxCursor"/>. Connection lifecycle, account and
/// transaction fetch, and webhook verify/parse are implemented against the MX Platform API.
///
/// Identity packing: MX scopes accounts/transactions by user_guid, but the contract passes a
/// single opaque id. The member ref's <c>ProviderId</c> is <c>{userGuid}|{memberGuid}</c> and each
/// account's <c>ProviderId</c> is <c>{userGuid}|{accountGuid}</c> (see <see cref="MxCompositeId"/>).
/// </summary>
public sealed partial class MxFinancialDataProvider : IFinancialDataProvider, IMemberIdResolver
{
    private readonly MxApiClient _client;
    private readonly MxWebhookVerifier _webhookVerifier;
    private readonly MxProviderOptions _options;
    private readonly ILogger<MxFinancialDataProvider> _logger;
    // Account currency by packed account id, so a transaction without its own currency inherits it instead of defaulting to USD (#124).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _currencyByAccount = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<string>? _webhookSyncEventTypes;

    /// <inheritdoc />
    public bool IsWebhookSyncTrigger(string? eventType) => MxWebhookEvents.IsSyncTrigger(eventType, _webhookSyncEventTypes);

    internal MxFinancialDataProvider(
        MxApiClient client,
        MxWebhookVerifier webhookVerifier,
        IOptions<MxProviderOptions> options,
        ILogger<MxFinancialDataProvider> logger,
        IReadOnlyList<string>? webhookSyncEventTypes = null)
    {
        _webhookSyncEventTypes = webhookSyncEventTypes;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _webhookVerifier = webhookVerifier ?? throw new ArgumentNullException(nameof(webhookVerifier));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    public string ProviderName => "MX";

    /// <summary>Member guid placeholder stored until the Connect widget has created the real member.</summary>
    internal const string PendingMember = "PENDING";

    /// <summary>
    /// Resolves the PENDING placeholder to the real member created by the Connect widget.
    /// Returns null when the user has not completed the widget yet.
    /// </summary>
    private async Task<string?> ResolveMemberGuidAsync(string userGuid, string memberGuid, CancellationToken ct)
    {
        if (!string.Equals(memberGuid, PendingMember, StringComparison.Ordinal))
        {
            return memberGuid;
        }

        var members = await _client.ListMembersAsync(userGuid, ct).ConfigureAwait(false);
        return members.FirstOrDefault(m => m.Guid is not null)?.Guid;
    }

    public async Task<string?> TryResolveMemberProviderIdAsync(string memberProviderId, CancellationToken cancellationToken = default)
    {
        var (userGuid, guid) = MxCompositeId.Unpack(memberProviderId);
        if (!string.Equals(guid, PendingMember, StringComparison.Ordinal))
        {
            return null;
        }

        var resolved = await ResolveMemberGuidAsync(userGuid, guid, cancellationToken).ConfigureAwait(false);
        return resolved is null ? null : MxCompositeId.Pack(userGuid, resolved);
    }

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts |
        ProviderCapabilities.IncrementalTransactions |
        ProviderCapabilities.FullTransactionHistory |
        ProviderCapabilities.WebhookVerification |
        ProviderCapabilities.WebhookParsing;

    /// <summary>Operator-held billable API (household tenant only); balances already report what is owed.</summary>
    public const ConnectorTraits DeclaredTraits = ConnectorTraits.BillablePerCall;

    public ConnectorTraits Traits => DeclaredTraits;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool HasMorePages(MxPagination? pagination, int currentPage)
    {
        if (pagination?.TotalPages is { } total)
        {
            var current = pagination.CurrentPage ?? currentPage;
            return current < total;
        }

        // No pagination metadata → assume this was the only page.
        return false;
    }

    private static void AddIfPresent(Dictionary<string, string> map, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[key] = value;
        }
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
