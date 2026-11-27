namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Remembers SHA-256 hashes of recently processed (already signature-verified) webhook bodies. The in-process map is a SINGLE-INSTANCE
/// first line only: it is bounded at <see cref="MaxEntries"/> (oldest entries are evicted once expired ones are gone, #179) and is not
/// shared between replicas or kept across restarts; the durable cross-instance check is the <c>IWebhookReplayStore</c> second line.
/// </summary>
public static class WebhookReplayGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    /// <summary>Hard cap on the in-process map.</summary>
    public const int MaxEntries = ApiLimits.WebhookReplayEntries;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    /// <summary>Entries currently held in the in-process map (for tests and diagnostics).</summary>
    internal static int TrackedCount => Seen.Count;

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
            if (logger is not null)
            {
                ApiLog.ReplayStoreUnavailable(logger, ex);
            }
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

            // Still full of live entries (a burst of distinct signed bodies): drop the oldest tenth rather than grow without bound.
            // The shared replay store remains the durable check for anything evicted here.
            if (Seen.Count >= MaxEntries)
            {
                foreach (var kv in Seen.OrderBy(kv => kv.Value).Take(MaxEntries / 10).ToList())
                {
                    Seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(kv.Key, kv.Value));
                }
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
