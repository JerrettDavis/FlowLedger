namespace FlowLedger.Api.Endpoints;

/// <summary>Remembers SHA-256 hashes of recently processed (already signature-verified) webhook bodies.</summary>
public static class WebhookReplayGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    /// <summary>
    /// Replay check with the shared store as the durable second line (#152): the in-process map answers first (cheap, per instance), then the
    /// store decides across instances and restarts. If the store is unreachable the webhook is processed (fail open): dropping a legitimate
    /// provider event is worse than the replay window shrinking to this process, and the event handlers are idempotent.
    /// </summary>
    public static async Task<bool> SeenRecentlyAsync(
        byte[] body, string provider, FlowLedger.Application.Abstractions.IWebhookReplayStore? store, CancellationToken ct,
        Microsoft.Extensions.Logging.ILogger? logger = null, DateTimeOffset? now = null)
    {
        if (SeenRecently(body, now))
        {
            return true;
        }

        if (store is null)
        {
            return false;
        }

        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
            return !await store.TryRecordAsync(provider, hash, Window, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Webhook replay store unavailable; relying on the in-process guard for this delivery.");
            return false;
        }
    }

    /// <summary>True when an identical body was already accepted inside the window (a replay). Per-process only.</summary>
    public static bool SeenRecently(byte[] body, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (Seen.Count >= MaxEntries)
        {
            foreach (var kv in Seen.Where(kv => t - kv.Value > Window).ToList())
            {
                Seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(kv.Key, kv.Value));
            }
        }

        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        if (Seen.TryAdd(key, t))
        {
            return false; // first sighting (atomic: exactly one concurrent caller wins)
        }

        while (Seen.TryGetValue(key, out var prior))
        {
            if (t - prior <= Window)
            {
                return true; // replay inside the window
            }

            // Expired: exactly one caller replaces the stale entry and is treated as new.
            if (Seen.TryUpdate(key, t, prior))
            {
                return false;
            }
        }

        return !Seen.TryAdd(key, t);
    }
}
