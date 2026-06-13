using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// Default <see cref="IFinancialDataProvider"/> implementation used when the real MX provider
/// is not enabled (i.e. <c>Mx:Enabled = false</c>).
///
/// Properties:
/// - Fully deterministic: same <c>TenantId</c> + seed always yields byte-identical output.
/// - No network calls, no API key required, zero cost.
/// - Configurable fault injection via <see cref="SimulatedProviderOptions"/>.
/// - Webhook verification accepts a fixed HMAC using a test secret ("test-webhook-secret").
/// </summary>
public sealed class SimulatedFinancialDataProvider : IFinancialDataProvider
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The fixed webhook secret used by the Simulated provider.
    /// Tests should use <see cref="BuildTestSignature"/> to generate valid signatures.
    /// This value must NEVER appear in non-test code paths.
    /// </summary>
    internal const string TestWebhookSecret = "sim-test-webhook-secret-do-not-use-in-production";

    private const string SimMemberId = "sim-member-001";

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly SimulatedProviderOptions _options;
    private int _callCount;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SimulatedFinancialDataProvider(IOptions<SimulatedProviderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    // ── IFinancialDataProvider ────────────────────────────────────────────────

    public string ProviderName => "Simulated";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Accounts |
        ProviderCapabilities.IncrementalTransactions |
        ProviderCapabilities.FullTransactionHistory |
        ProviderCapabilities.WebhookVerification |
        ProviderCapabilities.WebhookParsing |
        ProviderCapabilities.FaultSimulation;

    public async Task<ProviderMemberRef> BeginConnectionAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        await ApplySimulationAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderMemberRef(SimMemberId, "Simulated Bank", ConnectionStatus.Connected);
    }

    public async Task<ConnectionStatus> GetConnectionStatusAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        await ApplySimulationAsync(cancellationToken).ConfigureAwait(false);
        return ConnectionStatus.Healthy;
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(
        string memberProviderId,
        CancellationToken cancellationToken = default)
    {
        await ApplySimulationAsync(cancellationToken).ConfigureAwait(false);

        // TenantId is embedded in the memberProviderId for the simulated provider.
        // For simplicity, we use a fixed demo tenant when called from an unknown context.
        var tenantId = ParseTenantFromMemberId(memberProviderId);
        return SimulatedDataFactory.GetAccounts(tenantId, _options.BaseSeed);
    }

    public async Task<TransactionPage> GetTransactionsAsync(
        string providerAccountId,
        SyncCursor cursor,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        await ApplySimulationAsync(cancellationToken).ConfigureAwait(false);

        // Parse which tenant owns this account id — encoded as "sim-{type}-{tenantHex}-{random}"
        // For the simulated provider we derive the tenant from the account id prefix.
        var tenantId = ParseTenantFromAccountId(providerAccountId);
        var allTransactions = SimulatedDataFactory.GetTransactions(
            providerAccountId, tenantId, _options.BaseSeed, _options.HistoryMonths);

        // Cursor encodes the index into the ordered transaction list.
        var startIndex = ParseCursorIndex(cursor);
        var page = allTransactions.Skip(startIndex).Take(pageSize).ToList();
        var nextIndex = startIndex + page.Count;
        var hasMore = nextIndex < allTransactions.Count;
        var nextCursor = new SyncCursor(nextIndex.ToString());

        return new TransactionPage(page, nextCursor, hasMore);
    }

    public async Task VerifyWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        string signatureHeader,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var expected = BuildTestSignature(rawBody.Span);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader)))
        {
            throw new InvalidWebhookSignatureException(
                "Simulated webhook signature verification failed. " +
                $"Use {nameof(BuildTestSignature)} to generate a valid signature in tests.");
        }
    }

    public async Task<ProviderWebhookEvent> ParseWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var json = Encoding.UTF8.GetString(rawBody.Span);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FatalProviderException(
                "Simulated webhook payload is not valid JSON.", inner: ex);
        }
        var root = doc.RootElement;

        var eventType = root.TryGetProperty("event_type", out var et) ? et.GetString() ?? "UNKNOWN" : "UNKNOWN";
        var memberId = root.TryGetProperty("member_id", out var mid) ? mid.GetString() ?? SimMemberId : SimMemberId;
        var occurredAt = root.TryGetProperty("occurred_at", out var oat) && oat.GetString() is { } dateStr
            ? (DateTimeOffset.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow)
            : DateTimeOffset.UtcNow;

        var metadata = new Dictionary<string, string>();
        if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in meta.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }

        return new ProviderWebhookEvent(eventType, memberId, occurredAt, metadata);
    }

    // ── Public helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Generates the HMAC-SHA256 signature string that <see cref="VerifyWebhookAsync"/>
    /// expects.  Use this in tests to produce a valid signature for a test payload.
    /// </summary>
    public static string BuildTestSignature(ReadOnlySpan<byte> rawBody)
    {
        var keyBytes = Encoding.UTF8.GetBytes(TestWebhookSecret);
        var hash = HMACSHA256.HashData(keyBytes, rawBody);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Emits a synthetic webhook event payload that can be POSTed to the webhook endpoint
    /// in integration tests.
    /// </summary>
    public static (byte[] Body, string Signature) BuildSyntheticWebhookEvent(
        string eventType = "SYNC_COMPLETE",
        string memberId = SimMemberId)
    {
        var payload = new
        {
            event_type = eventType,
            member_id = memberId,
            occurred_at = DateTimeOffset.UtcNow.ToString("O"),
            metadata = new { source = "simulated" },
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var sig = BuildTestSignature(body);
        return (body, sig);
    }

    // ── Simulation helpers ────────────────────────────────────────────────────

    private async Task ApplySimulationAsync(CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        // Rate-limit check (before latency, so it fails fast)
        if (_options.RateLimitAfter > 0 && count > _options.RateLimitAfter)
        {
            throw new RateLimitedProviderException(
                $"Simulated rate limit exceeded after {_options.RateLimitAfter} calls.",
                retryAfter: DateTimeOffset.UtcNow.AddSeconds(30),
                providerCode: "RATE_LIMITED");
        }

        // Latency
        if (_options.LatencyMs > 0)
        {
            await Task.Delay(_options.LatencyMs, cancellationToken).ConfigureAwait(false);
        }

        // Random transient failure
        if (_options.FailureRate > 0.0)
        {
            // Use a stable per-call random — NOT Bogus here (we don't want seed coupling).
            var roll = Random.Shared.NextDouble();
            if (roll < _options.FailureRate)
            {
                throw new TransientProviderException(
                    $"Simulated transient failure (FailureRate={_options.FailureRate:P0}).",
                    retryAfter: TimeSpan.FromSeconds(5),
                    providerCode: "TRANSIENT");
            }
        }
    }

    // ── Cursor / ID helpers ───────────────────────────────────────────────────

    private static int ParseCursorIndex(SyncCursor cursor)
    {
        if (cursor.IsInitial)
        {
            return 0;
        }

        return int.TryParse(cursor.Value, out var idx) ? Math.Max(0, idx) : 0;
    }

    private static TenantId ParseTenantFromMemberId(string memberProviderId)
    {
        // Simulated member IDs are fixed; use a deterministic demo tenant.
        return TenantId.From(DemoTenantGuid);
    }

    private static TenantId ParseTenantFromAccountId(string providerAccountId)
    {
        // Account IDs are fully deterministic from the tenant; demo tenant is always used here
        // because the Simulated provider only supports a single demo context.
        return TenantId.From(DemoTenantGuid);
    }

    // ── Demo tenant constant ──────────────────────────────────────────────────

    /// <summary>
    /// Stable demo tenant guid.  NOT a secret — it's a fixed seed for demo data generation.
    /// </summary>
    private static readonly Guid DemoTenantGuid =
        new("00000000-0000-0000-0000-000000000001");
}
