namespace FlowLedger.Application.Abstractions;

/// <summary>
/// A write lost an optimistic-concurrency race: the row changed after the caller read it (#157). The API maps this to
/// HTTP 409 Conflict so the client can reload instead of silently overwriting the newer change.
/// </summary>
public sealed class ConcurrencyConflictException(string message, Exception? inner = null) : Exception(message, inner);
