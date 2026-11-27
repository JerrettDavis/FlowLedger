using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;

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
