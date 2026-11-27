using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Ids of the transactions one sync inserted or settled in place. Carried out in <c>SyncResult.ChangedTransactionIds</c> so the post-sync
/// steps (aliases, rules, enrichment) touch only these rows instead of re-reading the history (#163).
/// </summary>
internal sealed class SyncChangeSet
{
    private readonly List<Guid> _ids = [];

    public void Clear() => _ids.Clear();

    /// <summary>Records a settled row; always returns true so it can sit in a condition chain after <c>Settle(...)</c>.</summary>
    public bool Track(Transaction t)
    {
        _ids.Add(t.Id);
        return true;
    }

    public void AddRange(IEnumerable<Transaction> inserted) => _ids.AddRange(inserted.Select(t => t.Id));

    public IReadOnlyList<Guid> Snapshot() => _ids.Distinct().ToList();
}
