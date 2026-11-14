using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Categories;

/// <summary>Tracked-entity access for categorization (kept separate so the read-only repositories stay read-only).</summary>
public interface ICategorizationStore
{
    /// <summary>Newest-first tracked transactions (Planned/Skipped included; callers filter).</summary>
    Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}

public sealed record CategorizationResult(int Categorized, int ByLearnedMerchant, int ByKeyword, int Transfers, int StillUncategorized);

/// <summary>
/// Fills in MISSING categories only: a transaction that already has a category (manual or earlier run) is never changed.
/// Order of precedence for an uncategorized transaction:
///  1. transfer between the user's own accounts  -> "Transfer"
///  2. learned merchant: the category the user (or an earlier run) already gave that merchant most often
///  3. built-in keyword rules (<see cref="AutoCategorizer"/>)
/// Safe to re-run any time; it is idempotent.
/// </summary>
public sealed class CategorizationService(
    ICategorizationStore store,
    ICategoryRepository categories,
    IAccountRepository accounts,
    ITenantContext tenant)
{
    private const int Max = 50_000;

    public async Task<CategorizationResult> RunAsync(CancellationToken ct = default)
    {
        var txs = await store.LoadTrackedAsync(Max, ct);
        var real = txs.Where(t => t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped).ToList();
        var uncategorized = real.Where(t => t.CategoryId is null).ToList();
        if (uncategorized.Count == 0)
        {
            return new CategorizationResult(0, 0, 0, 0, 0);
        }

        var accountTypes = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = TransferDetector.Detect(real.Select(TransactionMapper.ToDto).ToList(), accountTypes);

        // merchant -> most common existing category
        var learned = real.Where(t => t.CategoryId is not null)
            .GroupBy(t => AutoCategorizer.MerchantKey(t.MerchantName, t.Description))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.CategoryId!.Value).OrderByDescending(x => x.Count()).First().Key);

        var existing = (await categories.ListAsync(ct)).ToDictionary(c => c.Path.Value, c => c, StringComparer.OrdinalIgnoreCase);
        var tenantId = TenantId.From(tenant.TenantId);
        async Task<CategoryId> EnsureAsync(string path)
        {
            if (existing.TryGetValue(path, out var c))
            {
                return c.CategoryId;
            }

            var created = Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last());
            await categories.AddAsync(created, ct);
            existing[path] = created;
            return created.CategoryId;
        }

        int byLearned = 0, byKeyword = 0, transfers = 0, left = 0;
        foreach (var t in uncategorized)
        {
            var key = AutoCategorizer.MerchantKey(t.MerchantName, t.Description);
            if (transferIds.Contains(t.Id))
            {
                t.Categorize(await EnsureAsync(AutoCategorizer.TransferCategory));
                transfers++;
            }
            else if (learned.TryGetValue(key, out var learnedId))
            {
                t.Categorize(learnedId);
                byLearned++;
            }
            else if (AutoCategorizer.Categorize(t.MerchantName, t.Description, t.Direction.ToString()) is { } path)
            {
                t.Categorize(await EnsureAsync(path));
                byKeyword++;
            }
            else
            {
                left++;
            }
        }

        await categories.SaveChangesAsync(ct);
        await store.SaveAsync(ct);
        return new CategorizationResult(byLearned + byKeyword + transfers, byLearned, byKeyword, transfers, left);
    }

    /// <summary>
    /// Sets one transaction's category (a manual choice, never overwritten later). With <paramref name="applyToMerchant"/>
    /// every still-UNCATEGORIZED transaction of the same merchant gets it too; already-categorized ones are left alone.
    /// </summary>
    public async Task<int> SetCategoryAsync(Guid transactionId, Guid categoryId, bool applyToMerchant, CancellationToken ct = default)
    {
        var txs = await store.LoadTrackedAsync(Max, ct);
        var target = txs.FirstOrDefault(t => t.Id == transactionId)
            ?? throw new KeyNotFoundException("Transaction not found.");
        var id = CategoryId.From(categoryId);
        target.Categorize(id);
        var changed = 1;

        if (applyToMerchant)
        {
            var key = AutoCategorizer.MerchantKey(target.MerchantName, target.Description);
            foreach (var t in txs.Where(t => t.Id != target.Id && t.CategoryId is null
                         && t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped
                         && AutoCategorizer.MerchantKey(t.MerchantName, t.Description) == key))
            {
                t.Categorize(id);
                changed++;
            }
        }

        await store.SaveAsync(ct);
        return changed;
    }
}
