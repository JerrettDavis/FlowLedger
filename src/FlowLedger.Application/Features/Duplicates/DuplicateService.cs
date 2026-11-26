using System.Text.Json;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Duplicates;

/// <summary>Persistence port for duplicate handling (tracked transactions, dismissals, and the merge log/tombstones).</summary>
public interface IDuplicateStore
{
    /// <summary>Tracked real transactions (Planned/Skipped excluded), newest first.</summary>
    Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default);

    Task<IReadOnlySet<string>> DismissedKeysAsync(CancellationToken ct = default);

    Task DismissAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Atomically removes <paramref name="duplicate"/> and records it (fingerprint tombstone + JSON snapshot) so syncs and
    /// imports never re-create it. The survivor's edits are saved in the same transaction.
    /// </summary>
    Task MergeAsync(Transaction survivor, Transaction duplicate, bool auto, string reason, string snapshotJson, CancellationToken ct = default);

    Task<IReadOnlyList<MergeLogEntry>> RecentMergesAsync(int take, CancellationToken ct = default);
}

public sealed record MergeLogEntry(Guid DuplicateId, Guid SurvivorId, DateTimeOffset MergedUtc, bool Auto, string Reason, string SnapshotJson);

public sealed record DuplicateSideDto(
    Guid Id, Guid AccountId, string Description, string? MerchantName, decimal Amount, string Direction, string Status,
    DateOnly EffectiveDate, Guid? CategoryId, string CategorySource, string? Notes, bool BankSynced);

public sealed record DuplicateSuggestionDto(
    string Key, string Confidence, double Score, int DaysApart, string Reason, DuplicateSideDto Keep, DuplicateSideDto Remove);

public sealed record DuplicateScanResult(int Total, int AutoMergeable, IReadOnlyList<DuplicateSuggestionDto> Suggestions);

public sealed record AutoMergeResult(int Merged, int Suggested);

public sealed class DuplicateOptions
{
    /// <summary>Merge High-confidence pairs automatically after every sync/import. Possible pairs are never auto-merged.</summary>
    public bool AutoMerge { get; set; } = true;
}

/// <summary>
/// Finds and folds duplicate transactions. Auto-merge touches only High-confidence pairs; everything else is a suggestion
/// the user accepts (<see cref="MergeAsync"/>) or dismisses. Nothing is silently lost: the removed row is snapshotted in the
/// merge log and its fingerprint is tombstoned so the next sync cannot bring it back.
/// </summary>
public sealed class DuplicateService(IDuplicateStore store, DuplicateOptions options)
{
    private const int Max = TransactionQuota.ProcessingCap;

    public async Task<DuplicateScanResult> ScanAsync(CancellationToken ct = default)
    {
        var pairs = await FindAsync(ct);
        var dtos = pairs.Select(ToDto).ToList();
        return new DuplicateScanResult(dtos.Count, dtos.Count(d => d.Confidence == nameof(DuplicateConfidence.High)), dtos);
    }

    /// <summary>Merges every High pair (when enabled). Safe to call after any ingest; a no-op on a clean ledger.</summary>
    public async Task<AutoMergeResult> AutoMergeAsync(CancellationToken ct = default)
    {
        var pairs = await FindAsync(ct);
        if (!options.AutoMerge)
        {
            return new AutoMergeResult(0, pairs.Count);
        }

        var merged = 0;
        foreach (var p in pairs.Where(p => p.Confidence == DuplicateConfidence.High))
        {
            ct.ThrowIfCancellationRequested();
            await MergePairAsync(p, auto: true, ct);
            merged++;
        }

        return new AutoMergeResult(merged, pairs.Count - merged);
    }

    /// <summary>Accepts a suggestion. <paramref name="keepId"/> overrides which row survives (null = the detector's choice).</summary>
    public async Task<bool> MergeAsync(Guid firstId, Guid secondId, Guid? keepId = null, CancellationToken ct = default)
    {
        var all = await store.LoadTrackedAsync(Max, ct);
        var a = all.FirstOrDefault(t => t.Id == firstId);
        var b = all.FirstOrDefault(t => t.Id == secondId);
        if (a is null || b is null || a.Id == b.Id || a.AccountId != b.AccountId)
        {
            return false;
        }

        if (a.Amount != b.Amount || a.Direction != b.Direction)
        {
            throw new ArgumentException("Only transactions with the same account, direction and amount can be merged.");
        }

        var (survivor, duplicate) = keepId switch
        {
            null => DuplicateDetector.ChooseSurvivor(a, b),
            var k when k == a.Id => (a, b),
            var k when k == b.Id => (b, a),
            _ => throw new ArgumentException("keepId must be one of the two transactions."),
        };

        if (duplicate.Status == TransactionStatus.Matched || duplicate.Status == TransactionStatus.Reconciled || duplicate.MatchedOccurrenceId is not null)
        {
            if (survivor.Status is TransactionStatus.Matched or TransactionStatus.Reconciled || survivor.MatchedOccurrenceId is not null)
            {
                throw new InvalidOperationException("Both transactions are matched to a plan or reconciled; unmatch one first.");
            }

            // The plan link lives on the removed row; keep that one instead of orphaning the occurrence.
            (survivor, duplicate) = (duplicate, survivor);
        }

        await MergePairAsync(new DuplicatePair(survivor, duplicate, DuplicateConfidence.Possible, 1, Math.Abs(a.EffectiveDate.DayNumber - b.EffectiveDate.DayNumber), "Merged by hand"), auto: false, ct);
        return true;
    }

    public Task DismissAsync(Guid firstId, Guid secondId, CancellationToken ct = default)
        => store.DismissAsync(DuplicatePair.MakeKey(firstId, secondId), ct);

    public Task<IReadOnlyList<MergeLogEntry>> RecentMergesAsync(int take = 50, CancellationToken ct = default)
        => store.RecentMergesAsync(Math.Clamp(take, 1, 500), ct);

    private async Task<IReadOnlyList<DuplicatePair>> FindAsync(CancellationToken ct)
    {
        var dismissed = await store.DismissedKeysAsync(ct);
        var rows = await store.LoadTrackedAsync(Max, ct);
        return DuplicateDetector.Find(rows).Where(p => !dismissed.Contains(p.Key)).ToList();
    }

    private async Task MergePairAsync(DuplicatePair p, bool auto, CancellationToken ct)
    {
        var keep = p.Survivor;
        var drop = p.Duplicate;
        var snapshot = JsonSerializer.Serialize(new
        {
            drop.Id,
            AccountId = drop.AccountId.Value,
            Amount = drop.Amount.Amount,
            Direction = drop.Direction.ToString(),
            drop.Description,
            drop.MerchantName,
            Status = drop.Status.ToString(),
            drop.EffectiveDate,
            drop.PostedDate,
            CategoryId = drop.CategoryId?.Value,
            CategorySource = drop.CategorySource.ToString(),
            drop.Notes,
            Fingerprint = drop.Fingerprint?.Value,
            Splits = drop.Splits.Select(s => new { s.Amount.Amount, CategoryId = s.CategoryId?.Value, s.Notes }),
        });

        // Fold what the user or the bank knew into the survivor.
        if (drop.CategoryId is not null
            && (keep.CategoryId is null
                || (keep.CategorySource != CategorySource.Manual && drop.CategorySource == CategorySource.Manual)))
        {
            keep.Categorize(drop.CategoryId.Value, drop.MerchantName ?? keep.MerchantName, drop.CategorySource, drop.CategoryConfidence);
        }
        else if (keep.MerchantName is null && drop.MerchantName is not null)
        {
            keep.SetMerchantName(drop.MerchantName);
        }

        keep.AppendNote(drop.Notes);
        if (keep.Splits.Count == 0 && drop.Splits.Count > 0)
        {
            keep.SetSplits(drop.Splits.Select(s => new TransactionSplit(s.Amount, s.CategoryId, s.Notes)));
        }

        if (keep.Status == TransactionStatus.Pending && drop.Status != TransactionStatus.Pending && drop.PostedDate is { } posted)
        {
            keep.Settle(posted);
        }

        await store.MergeAsync(keep, drop, auto, p.Reason, snapshot, ct);
    }

    private static DuplicateSuggestionDto ToDto(DuplicatePair p) =>
        new(p.Key, p.Confidence.ToString(), p.Score, p.DaysApart, p.Reason, Side(p.Survivor), Side(p.Duplicate));

    private static DuplicateSideDto Side(Transaction t) => new(
        t.Id, t.AccountId.Value, t.Description, t.MerchantName, t.Amount.Amount, t.Direction.ToString(), t.Status.ToString(),
        t.EffectiveDate, t.CategoryId?.Value, t.CategorySource.ToString(), t.Notes,
        t.Fingerprint?.Value.StartsWith("pid:", StringComparison.Ordinal) == true);
}
