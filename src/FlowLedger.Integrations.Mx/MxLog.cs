using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Mx;

/// <summary>Source-generated log messages for static MX helpers that receive an optional logger.</summary>
internal static partial class MxLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message =
        "MX sync cursor decoded to null; pagination is restarting from page 1. " +
        "This may cause duplicate imports. Cursor value length: {Length}.")]
    internal static partial void CursorDecodedToNull(ILogger logger, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "MX sync cursor could not be base64-decoded; pagination is restarting from page 1. " +
        "This may cause duplicate imports. Cursor value length: {Length}.")]
    internal static partial void CursorNotBase64(ILogger logger, Exception exception, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "MX sync cursor JSON is malformed; pagination is restarting from page 1. " +
        "This may cause duplicate imports. Cursor value length: {Length}.")]
    internal static partial void CursorMalformedJson(ILogger logger, Exception exception, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Transaction {TransactionGuid} has no valid date fields (date={Date}, posted_at={PostedAt}, transacted_at={TransactedAt}); " +
        "defaulting to DateOnly.MinValue.")]
    internal static partial void TransactionHasNoValidDate(
        ILogger logger, string? transactionGuid, string? date, string? postedAt, string? transactedAt);
}
