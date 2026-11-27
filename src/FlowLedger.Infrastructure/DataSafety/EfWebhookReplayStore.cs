using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// EF-backed <see cref="IWebhookReplayStore"/> (#152). One row per (provider, body hash); the insert is atomic, so exactly one of
/// several concurrent deliveries of the same body (on any instance) is treated as new. Holds hashes and timestamps only, no payload and
/// no tenant data (host-wide, through <see cref="HostDb"/>); old rows are pruned opportunistically.
/// </summary>
internal sealed class EfWebhookReplayStore(HostDb host) : IWebhookReplayStore
{
    public async Task<bool> TryRecordAsync(string provider, string bodyHashHex, TimeSpan window, CancellationToken ct = default)
    {
        await using var db = host.Create();

        // Inserted (new) or refreshed after expiry => one row is affected. A live duplicate matches the conflict but fails the WHERE => none.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO webhook_replays (provider, body_hash, seen_utc) VALUES ({provider}, {bodyHashHex}, now())
               ON CONFLICT (provider, body_hash) DO UPDATE SET seen_utc = now() WHERE webhook_replays.seen_utc < now() - {window}", ct);
        var isNew = affected > 0;

        if (isNew && Random.Shared.Next(50) == 0)
        {
            var cutoff = DateTimeOffset.UtcNow - (window * 6);
            await db.WebhookReplays.Where(r => r.SeenUtc < cutoff).ExecuteDeleteAsync(ct);
        }

        return isNew;
    }
}
