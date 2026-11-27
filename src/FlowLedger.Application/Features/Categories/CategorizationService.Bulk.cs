using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Categories;

public sealed partial class CategorizationService
{
    /// <summary>Most rows one bulk edit may change.</summary>
    public const int MaxBulkRows = 500;

    /// <summary>
    /// Sets the same category (as a manual choice) on every listed transaction of this tenant (the grid's bulk edit, #67).
    /// Unknown ids are skipped rather than failing the batch. Returns how many rows changed.
    /// </summary>
    public async Task<int> SetCategoryBulkAsync(IReadOnlyCollection<Guid> transactionIds, Guid categoryId, CancellationToken ct = default)
    {
        if (transactionIds.Count == 0)
        {
            return 0;
        }

        if (transactionIds.Count > MaxBulkRows)
        {
            throw new RequestValidationException("transactionIds", $"Select at most {MaxBulkRows} transactions at a time.");
        }

        var category = (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == categoryId)
            ?? throw new NotFoundException("Category not found.");
        var changed = 0;
        foreach (var id in transactionIds.Distinct())
        {
            if (await store.FindTrackedAsync(id, ct) is { } tx && !(tx.CategoryId == category.CategoryId && tx.CategorySource == CategorySource.Manual))
            {
                tx.Categorize(category.CategoryId);
                changed++;
            }
        }

        await store.SaveAsync(ct);
        return changed;
    }

    /// <summary>
    /// How many OTHER transactions "apply to same merchant" would re-label right now (uncategorized or auto-categorized, real, same
    /// merchant): the exact figure the confirmation shows, counted over the whole history rather than the rows on screen.
    /// </summary>
    public async Task<int> CountSameMerchantAsync(Guid transactionId, CancellationToken ct = default)
    {
        var target = await store.FindTrackedAsync(transactionId, ct)
            ?? throw new NotFoundException("Transaction not found.");
        return (await MerchantCandidatesAsync(target, ct)).Count;
    }

    /// <summary>
    /// Undoes a category change: puts each row back as <paramref name="restore"/> says, but only while the row still carries
    /// <paramref name="appliedCategoryId"/> (a row the user has re-categorized since is left alone). Returns how many rows were restored.
    /// </summary>
    public async Task<int> RestoreCategoriesAsync(Guid appliedCategoryId, IReadOnlyCollection<CategoryRestore> restore, CancellationToken ct = default)
    {
        if (restore.Count == 0)
        {
            return 0;
        }

        if (restore.Count > MaxRestoreRows)
        {
            throw new RequestValidationException("items", $"Restore at most {MaxRestoreRows} transactions at a time.");
        }

        var applied = CategoryId.From(appliedCategoryId);
        var byId = (await store.LoadTrackedUncategorizedOrAutoAsync(Max, ct)).ToDictionary(t => t.Id);
        var restored = 0;
        foreach (var item in restore.DistinctBy(r => r.TransactionId))
        {
            if (!byId.TryGetValue(item.TransactionId, out var tx))
            {
                tx = await store.FindTrackedAsync(item.TransactionId, ct);
            }

            if (tx is null || tx.CategoryId != applied)
            {
                continue;
            }

            if (item.CategoryId is { } previous)
            {
                // Only a category of this tenant may be written back.
                if ((await categories.ListAsync(ct)).All(c => c.Id != previous))
                {
                    continue;
                }

                tx.RestoreCategory(CategoryId.From(previous), item.Manual ? CategorySource.Manual : CategorySource.Auto, item.Confidence);
            }
            else
            {
                tx.RestoreCategory(null, CategorySource.Manual, null);
            }

            restored++;
        }

        await store.SaveAsync(ct);
        return restored;
    }

    /// <summary>Most rows one undo may restore.</summary>
    public const int MaxRestoreRows = 5000;

    private static CategoryRestore Snapshot(Transaction t)
        => new(t.Id, t.CategoryId?.Value, t.CategorySource == CategorySource.Manual, t.CategoryConfidence);

    private async Task<List<Transaction>> MerchantCandidatesAsync(Transaction target, CancellationToken ct)
    {
        var key = AutoCategorizer.MerchantKey(target.MerchantName, target.Description);
        return (await store.LoadTrackedUncategorizedOrAutoAsync(Max, ct))
            .Where(t => t.Id != target.Id
                && (t.CategoryId is null || t.CategorySource == CategorySource.Auto)
                && t.Status.IsReal()
                && AutoCategorizer.MerchantKey(t.MerchantName, t.Description) == key)
            .ToList();
    }

    /// <summary>
    /// Persists the pairing (#68): both legs of a detected transfer share one link id, so later reads need no re-detection.
    /// Legs that changed are added to <paramref name="changedRows"/>. Returns how many legs were newly linked.
    /// </summary>
    private static int LinkTransferLegs(
        List<(Guid A, Guid B)> pairs, List<Transaction> context, List<Transaction> changedRows, HashSet<Guid> changedIds)
    {
        var linked = 0;
        var byId = context.ToDictionary(t => t.Id);
        foreach (var (a, b) in pairs)
        {
            var link = Transactions.TransferDetector.LinkIdFor(a, b);
            foreach (var id in new[] { a, b })
            {
                if (byId.TryGetValue(id, out var leg) && leg.LinkTransfer(link))
                {
                    linked++;
                    if (changedIds.Add(leg.Id))
                    {
                        changedRows.Add(leg);
                    }
                }
            }
        }

        return linked;
    }
}
