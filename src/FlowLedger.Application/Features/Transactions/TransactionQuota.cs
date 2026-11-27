using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>Thrown when adding transactions would exceed the per-tenant cap. Subclasses <see cref="InvalidOperationException"/> so existing import handling reports it as a 400.</summary>
public sealed class TransactionQuotaExceededException(string message) : InvalidOperationException(message);

/// <summary>Per-tenant ceiling on stored transactions so one account cannot grow the database without bound.</summary>
public static class TransactionQuota
{
    public const int MaxPerTenant = 500_000;

    /// <summary>
    /// The single bound every whole-history engine (categorize, rules, duplicates, insights, query, import, export) loads in one pass.
    /// It is deliberately a named constant, not a scattering of literals, so the processing window and the quota cannot silently
    /// drift apart.
    /// </summary>
    public const int ProcessingCap = 50_000;

    /// <summary>
    /// True when a whole-history engine that loaded <paramref name="loaded"/> newest-first rows hit <see cref="ProcessingCap"/>, so older rows
    /// were left out of its answer. Engines surface this as a <c>Partial</c> flag instead of silently dropping the oldest history (#166).
    /// </summary>
    public static bool IsPartial(int loaded) => loaded >= ProcessingCap;

    public static async Task EnsureRoomAsync(ITransactionRepository repo, int adding, CancellationToken ct = default)
        => EnsureRoom(await repo.CountAsync(ct), adding);

    /// <summary>Throws <see cref="TransactionQuotaExceededException"/> when <paramref name="current"/> stored rows plus <paramref name="adding"/> would pass the cap.</summary>
    public static void EnsureRoom(int current, int adding, int max = MaxPerTenant)
    {
        if (current + adding > max)
        {
            throw new TransactionQuotaExceededException(
                $"This would exceed the limit of {max:N0} transactions per user ({current:N0} stored). Remove old data or contact the administrator.");
        }
    }
}
