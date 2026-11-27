using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Mx;

/// <summary>Source-generated log messages (CA1848).</summary>
public sealed partial class MxFinancialDataProvider
{
    [LoggerMessage(Level = LogLevel.Information, Message =
        "MX member {MemberGuid} is aggregating or transient (status={Status}, isAggregating={IsAgg}). " +
        "Polling status up to {MaxAttempts} times with {IntervalMs}ms interval.")]
    private partial void LogMemberPolling(string memberGuid, string? status, bool? isAgg, int maxAttempts, int intervalMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "MX member {MemberGuid} is ready after {Attempt} poll(s).")]
    private partial void LogMemberReady(string memberGuid, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message =
        "MX member {MemberGuid} poll {Attempt}/{MaxAttempts}: status={Status}, isAggregating={IsAgg}.")]
    private partial void LogMemberPollAttempt(string memberGuid, int attempt, int maxAttempts, string? status, bool? isAgg);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping MX account: {Message}")]
    private partial void LogSkippingAccount(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "GetAccountsAsync: reached MaxPages limit ({MaxPages}) for member {MemberGuid}. " +
        "Returning {Count} accounts collected so far. Remaining pages skipped.")]
    private partial void LogAccountsMaxPages(int maxPages, string memberGuid, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping MX transaction: {Message}")]
    private partial void LogSkippingTransaction(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping MX transaction {ProviderTransactionId}: no usable date.")]
    private partial void LogSkippingTransactionNoDate(string? providerTransactionId);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "GetTransactionsAsync: reached MaxPages limit ({MaxPages}); resuming on the next sync.")]
    private partial void LogTransactionsMaxPages(int maxPages);

    [LoggerMessage(Level = LogLevel.Error, Message = "MX webhook payload is malformed and could not be parsed.")]
    private partial void LogWebhookMalformed(Exception exception);
}
