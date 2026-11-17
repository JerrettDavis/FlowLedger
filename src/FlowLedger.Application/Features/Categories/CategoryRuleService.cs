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

        await store.SetAsync(key, path, ct);
        return await ApplyAsync(ct);
    }

    public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => store.RemoveAsync(NormalizePattern(pattern), ct);

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
