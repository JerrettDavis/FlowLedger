using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>Thrown when adding transactions would exceed the per-tenant cap. Subclasses <see cref="InvalidOperationException"/> so existing import handling reports it as a 400.</summary>
public sealed class TransactionQuotaExceededException(string message) : InvalidOperationException(message);

/// <summary>Per-tenant ceiling on stored transactions so one account cannot grow the database without bound.</summary>
public static class TransactionQuota
{
    public const int MaxPerTenant = 500_000;

    public static async Task EnsureRoomAsync(ITransactionRepository repo, int adding, CancellationToken ct = default)
    {
        var current = await repo.CountAsync(ct);
        if (current + adding > MaxPerTenant)
        {
            throw new TransactionQuotaExceededException(
                $"This would exceed the limit of {MaxPerTenant:N0} transactions per user ({current:N0} stored). Remove old data or contact the administrator.");
        }
    }
}
