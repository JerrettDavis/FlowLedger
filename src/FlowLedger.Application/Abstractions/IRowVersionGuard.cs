namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Optimistic-concurrency precondition for a write (#157): the caller read the row at <c>expectedVersion</c> (the ETag it sent as
/// <c>If-Match</c>). The next save fails with a concurrency conflict (HTTP 409) if the stored row has moved on, even when only
/// child rows (such as splits) change. A null version keeps last-write-wins, so existing callers are unaffected.
/// </summary>
public interface IRowVersionGuard
{
    void Expect(object entity, uint? expectedVersion);
}
