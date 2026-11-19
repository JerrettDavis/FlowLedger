namespace FlowLedger.Application.Features.HomeAssistant;

/// <summary>Per-household Home Assistant settings, the webhook secret and the "already sent" ledger.</summary>
public interface IHomeAssistantStore
{
    /// <summary>False when no encryption key is configured: the webhook URL is a secret and is never stored in clear text.</summary>
    bool CanStoreWebhook { get; }

    /// <summary>The settings read model. Never contains the webhook URL.</summary>
    Task<HomeAssistantSettings> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>The decrypted webhook URL, for sending only. Null when none is stored. Never expose this through the API.</summary>
    Task<string?> GetWebhookUrlAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Saves settings. <paramref name="webhookUrl"/> null keeps the stored one; <paramref name="clearWebhook"/> removes it.</summary>
    Task SaveAsync(Guid tenantId, bool enabled, string? webhookUrl, bool clearWebhook, bool shareAmounts, bool includeNames,
        int leadDays, IReadOnlyList<string> kinds, CancellationToken ct = default);

    /// <summary>Atomically claims a dedupe key; false when it was already sent (so the caller skips it).</summary>
    Task<bool> TryClaimAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default);

    /// <summary>Releases a claim after a failed send so the next run retries it.</summary>
    Task ReleaseAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default);

    /// <summary>Records the outcome of the latest delivery attempt (<paramref name="error"/> null on success).</summary>
    Task RecordResultAsync(Guid tenantId, string? error, CancellationToken ct = default);

    /// <summary>Forgets claims older than <paramref name="olderThan"/> so the ledger stays small.</summary>
    Task PruneAsync(Guid tenantId, DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>Delivers one event payload to a Home Assistant webhook.</summary>
public interface IHomeAssistantSender
{
    /// <returns>Null on success, otherwise a short, safe error summary (never the URL or response body).</returns>
    Task<string?> SendAsync(string webhookUrl, object payload, bool allowLocalNetwork, CancellationToken ct = default);
}

/// <summary>Decides whether a tenant may send to local-network addresses (only the household owner may).</summary>
public interface IHomeAssistantNetworkPolicy
{
    bool AllowLocalNetwork(Guid tenantId);
}
