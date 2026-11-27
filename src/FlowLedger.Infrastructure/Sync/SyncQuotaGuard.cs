using FlowLedger.Application.Features.Transactions;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Applies the per-tenant transaction quota to provider inserts (#166): a sync that would pass it stops with a classified, user-visible error.</summary>
internal static partial class SyncQuotaGuard
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync stopped: {Message}")]
    private static partial void LogSyncStopped(ILogger logger, string message);

    public static void Ensure(ILogger logger, int stored, int adding, int max)
    {
        try
        {
            TransactionQuota.EnsureRoom(stored, adding, max);
        }
        catch (TransactionQuotaExceededException ex)
        {
            LogSyncStopped(logger, ex.Message);
            throw new NeedsUserActionProviderException(
                ex.Message, "Remove old transactions (or ask the administrator to raise the limit), then sync again.", "transaction_quota_exceeded")
            {
                Kind = ProviderIssueKind.Internal,
            };
        }
    }
}
