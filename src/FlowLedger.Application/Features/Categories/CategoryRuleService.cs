using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Categories;

/// <summary>A user-defined rule: a transaction whose description/merchant contains <see cref="Pattern"/> gets category <see cref="CategoryPath"/>.</summary>
public sealed record CategoryRuleDto(string Pattern, string CategoryPath);

public interface ICategoryRuleStore
{
    Task<IReadOnlyList<CategoryRuleDto>> ListAsync(CancellationToken ct = default);
    Task SetAsync(string pattern, string categoryPath, CancellationToken ct = default);
    Task<bool> RemoveAsync(string pattern, CancellationToken ct = default);
}

/// <summary>
/// User-defined categorization rules (Monarch/Simplifi style). The longest matching pattern wins. Rules fill uncategorized
/// transactions and replace automatic categories, but never override a category the user chose by hand.
/// </summary>
public sealed class CategoryRuleService(
    ICategoryRuleStore store,
    ICategorizationStore transactions,
    ICategoryRepository categories,
    ITenantContext tenant)
{
    private const int Max = 50_000;
    public const int MaxLength = 200;
    public const int MaxRules = 1000;
    public const int MaxNoteLength = 1000;

    public static string NormalizePattern(string? pattern) => MerchantAliasService.NormalizePattern(pattern);

    /// <summary>Returns the rule that applies to the text (longest pattern wins), or null.</summary>
    public static CategoryRuleDto? Match(IEnumerable<CategoryRuleDto> rules, string? description, string? merchant)
    {
        var haystack = NormalizePattern(description + " " + merchant);
        return rules.OrderByDescending(r => r.Pattern.Length)
            .FirstOrDefault(r => r.Pattern.Length > 0 && haystack.Contains(r.Pattern, StringComparison.Ordinal));
    }

    public Task<IReadOnlyList<CategoryRuleDto>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public async Task<int> SetAsync(string pattern, string categoryPath, CancellationToken ct = default)
    {
        var key = NormalizePattern(pattern);
        var path = (categoryPath ?? string.Empty).Trim().Trim('/');
        if (key.Length == 0 || path.Length == 0)
        {
            throw new ArgumentException("Both the text to match and a category are required.");
        }

        if (key.Length > MaxLength || path.Length > MaxLength)
        {
            throw new ArgumentException($"Pattern and category must be {MaxLength} characters or fewer.");
        }

        var existing = await store.ListAsync(ct);
        if (existing.Count >= MaxRules && !existing.Any(r => string.Equals(r.Pattern, key, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"You can have at most {MaxRules} rules. Remove one before adding another.");
        }

        await store.SetAsync(key, path, ct);
        return await ApplyAsync(ct);
    }

    public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => store.RemoveAsync(NormalizePattern(pattern), ct);

    /// <summary>
    /// Mint-style "recategorize": sets one transaction's category by hand and, when <paramref name="applyToAll"/>, also
    /// re-labels EVERY transaction of the same merchant (including ones categorized by hand earlier) and saves a rule so
    /// future syncs of that merchant land in the same category. Returns how many transactions changed.
    /// </summary>
    public async Task<int> RecategorizeAsync(Guid transactionId, Guid categoryId, bool applyToAll, CancellationToken ct = default, string? note = null)
    {
        if (note?.Trim() is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"Notes are limited to {MaxNoteLength} characters.");
        }

        var target = await transactions.FindTrackedAsync(transactionId, ct)
            ?? throw new KeyNotFoundException("Transaction not found.");
        var category = (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == categoryId)
            ?? throw new KeyNotFoundException("Category not found.");
        var id = category.CategoryId;

        var changed = target.CategoryId == id && target.CategorySource == CategorySource.Manual ? 0 : 1;
        target.Categorize(id);
        target.AppendNote(note);

        string? pattern = null;
        if (applyToAll)
        {
            var key = AutoCategorizer.MerchantKey(target.MerchantName, target.Description);
            if (key.Length > 0)
            {
                foreach (var t in await transactions.LoadTrackedAsync(Max, ct))
                {
                    if (t.Id == target.Id || t.Status is TransactionStatus.Planned or TransactionStatus.Skipped
                        || AutoCategorizer.MerchantKey(t.MerchantName, t.Description) != key)
                    {
                        continue;
                    }

                    if (t.CategoryId != id || t.CategorySource != CategorySource.Manual)
                    {
                        t.Categorize(id);
                        changed++;
                    }
                }

                pattern = NormalizePattern(key);
            }
        }

        await transactions.SaveAsync(ct);

        if (pattern is { Length: > 0 and <= MaxLength })
        {
            await store.SetAsync(pattern, category.Path.Value, ct);
        }

        return changed;
    }

    /// <summary>Applies all rules. Idempotent; returns how many transactions changed category.</summary>
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var rules = await store.ListAsync(ct);
        if (rules.Count == 0)
        {
            return 0;
        }

        var existing = (await categories.ListAsync(ct)).ToDictionary(c => c.Path.Value, c => c, StringComparer.OrdinalIgnoreCase);
        var tenantId = TenantId.From(tenant.TenantId);
        var changed = 0;
        var createdCategory = false;

        foreach (var t in await transactions.LoadTrackedAsync(Max, ct))
        {
            if (t.Status is TransactionStatus.Planned or TransactionStatus.Skipped)
            {
                continue;
            }

            if (t.CategoryId is not null && t.CategorySource == CategorySource.Manual)
            {
                continue;
            }

            var rule = Match(rules, t.Description, t.MerchantName);
            if (rule is null)
            {
                continue;
            }

            if (!existing.TryGetValue(rule.CategoryPath, out var category))
            {
                category = Category.Create(tenantId, new CategoryPath(rule.CategoryPath), rule.CategoryPath.Split('/').Last());
                await categories.AddAsync(category, ct);
                existing[rule.CategoryPath] = category;
                createdCategory = true;
            }

            if (t.CategoryId == category.CategoryId)
            {
                continue;
            }

            t.Categorize(category.CategoryId, null, CategorySource.Auto);
            changed++;
        }

        if (createdCategory)
        {
            await categories.SaveChangesAsync(ct);
        }

        if (changed > 0)
        {
            await transactions.SaveAsync(ct);
        }

        return changed;
    }
}
