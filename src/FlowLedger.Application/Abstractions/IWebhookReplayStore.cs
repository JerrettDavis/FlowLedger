namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Shared record of recently processed (already signature-verified) webhook bodies, so a replay is detected across API instances and
/// restarts (#152). The in-process guard stays as the fast path; this is the durable second line.
/// </summary>
public interface IWebhookReplayStore
{
    /// <summary>
    /// Records the body hash for the provider. True when it is new (first sighting, or the previous one is older than
    /// <paramref name="window"/>); false when an identical body was already accepted inside the window (a replay).
    /// </summary>
    Task<bool> TryRecordAsync(string provider, string bodyHashHex, TimeSpan window, CancellationToken ct = default);
}
