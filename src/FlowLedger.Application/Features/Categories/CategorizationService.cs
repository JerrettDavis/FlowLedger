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

    /// <summary>One tracked transaction by id (null when absent). Default falls back to a full load for simple stores.</summary>
    async Task<Transaction?> FindTrackedAsync(Guid id, CancellationToken ct = default)
        => (await LoadTrackedAsync(50_000, ct)).FirstOrDefault(t => t.Id == id);

    /// <summary>Tracked real transactions that are uncategorized or auto-categorized (callers still filter precisely).</summary>
    async Task<IReadOnlyList<Transaction>> LoadTrackedUncategorizedOrAutoAsync(int max, CancellationToken ct = default)
        => await LoadTrackedAsync(max, ct);

    /// <summary>Tracked transactions whose category was set automatically.</summary>
    async Task<IReadOnlyList<Transaction>> LoadTrackedAutoCategorizedAsync(int max, CancellationToken ct = default)
        => await LoadTrackedAsync(max, ct);

    /// <summary>True when some real (not Planned/Skipped) transaction has no category.</summary>
    async Task<bool> AnyUncategorizedAsync(CancellationToken ct = default)
        => (await LoadTrackedAsync(50_000, ct)).Any(t => t.CategoryId is null
            && t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped);
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
        if (!await store.AnyUncategorizedAsync(ct))
        {
            return new CategorizationResult(0, 0, 0, 0, 0);
        }

        var txs = await store.LoadTrackedAsync(Max, ct);
        var real = txs.Where(t => t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped).ToList();
        var uncategorized = real.Where(t => t.CategoryId is null).ToList();
        if (uncategorized.Count == 0)
        {
            return new CategorizationResult(0, 0, 0, 0, 0);
        }

        var accountTypes = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = TransferDetector.Detect(real.Select(TransactionMapper.ToDto).ToList(), accountTypes);

        // merchant -> the category the USER chose most often (auto-filled rows are not evidence of intent)
        var learned = real.Where(t => t.CategoryId is not null && t.CategorySource == CategorySource.Manual)
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
                t.Categorize(await EnsureAsync(AutoCategorizer.TransferCategory), null, CategorySource.Auto);
                transfers++;
            }
            else if (learned.TryGetValue(key, out var learnedId))
            {
                t.Categorize(learnedId, null, CategorySource.Auto);
                byLearned++;
            }
            else if (AutoCategorizer.Categorize(t.MerchantName, t.Description, t.Direction.ToString()) is { } path)
            {
                t.Categorize(await EnsureAsync(path), null, CategorySource.Auto);
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
    /// every other transaction of the same merchant that is uncategorized OR was categorized automatically gets it too
    /// (they become Auto, so a later correction can still replace them); categories the user chose by hand are left alone.
    /// </summary>
    public async Task<int> SetCategoryAsync(Guid transactionId, Guid categoryId, bool applyToMerchant, CancellationToken ct = default)
    {
        var target = await store.FindTrackedAsync(transactionId, ct)
            ?? throw new KeyNotFoundException("Transaction not found.");
        // The id comes from the request body: it must be one of THIS tenant's categories (the repository is tenant-scoped).
        var category = (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == categoryId)
            ?? throw new KeyNotFoundException("Category not found.");
        var id = category.CategoryId;
        target.Categorize(id);
        var changed = 1;

        if (applyToMerchant)
        {
            var key = AutoCategorizer.MerchantKey(target.MerchantName, target.Description);
            var candidates = await store.LoadTrackedUncategorizedOrAutoAsync(Max, ct);
            foreach (var t in candidates.Where(t => t.Id != target.Id
                         && (t.CategoryId is null || t.CategorySource == CategorySource.Auto)
                         && t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped
                         && AutoCategorizer.MerchantKey(t.MerchantName, t.Description) == key))
            {
                t.Categorize(id, null, CategorySource.Auto);
                changed++;
            }
        }

        await store.SaveAsync(ct);
        return changed;
    }

    /// <summary>Clears every category the auto-categorizer set (manual categories are untouched) so it can be re-run with better rules.</summary>
    public async Task<int> ClearAutoCategoriesAsync(CancellationToken ct = default)
    {
        var txs = await store.LoadTrackedAutoCategorizedAsync(Max, ct);
        var cleared = txs.Count(t => t.ClearAutoCategory());
        await store.SaveAsync(ct);
        return cleared;
    }
}
