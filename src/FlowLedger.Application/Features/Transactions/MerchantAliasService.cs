using FlowLedger.Application.Features.Categories;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>A user-defined rule: any transaction whose bank description contains <see cref="Pattern"/> shows as <see cref="Alias"/>.</summary>
public sealed record MerchantAliasDto(string Pattern, string Alias);

/// <summary>Per-tenant persistence for alias rules. Patterns are stored normalized (see <see cref="MerchantAliasService.NormalizePattern"/>).</summary>
public interface IMerchantAliasStore
{
    Task<IReadOnlyList<MerchantAliasDto>> ListAsync(CancellationToken ct = default);
    Task SetAsync(string pattern, string alias, CancellationToken ct = default);
    Task<bool> RemoveAsync(string pattern, CancellationToken ct = default);
}

/// <summary>
/// Custom labels for merchants. A bank may bill "NANONOBLE PTE. LTD. SINGAPORE SGP"; the user can alias it to "MiniMax".
/// Rules match case-insensitively on a substring of the ORIGINAL bank description (which is never altered), and the alias
/// is stored as the transaction's merchant name, so it shows in every view, report and search. Rules are re-applied after
/// each sync so new transactions pick them up. The first (longest) matching pattern wins.
/// </summary>
public sealed class MerchantAliasService(IMerchantAliasStore store, ICategorizationStore transactions)
{
    private const int Max = 50_000;
    public const int MaxLength = 200;

    public static string NormalizePattern(string? pattern)
        => string.Join(' ', (pattern ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public Task<IReadOnlyList<MerchantAliasDto>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    /// <summary>Saves (or replaces) a rule and applies it to existing transactions. Returns how many transactions changed.</summary>
    public async Task<int> SetAsync(string pattern, string alias, CancellationToken ct = default)
    {
        var key = NormalizePattern(pattern);
        var name = alias?.Trim() ?? string.Empty;
        if (key.Length == 0 || name.Length == 0)
        {
            throw new ArgumentException("Both the text to match and the alias are required.");
        }

        if (key.Length > MaxLength || name.Length > MaxLength)
        {
            throw new ArgumentException($"Pattern and alias must be {MaxLength} characters or fewer.");
        }

        await store.SetAsync(key, name, ct);
        return await ApplyAsync(ct);
    }

    public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => store.RemoveAsync(NormalizePattern(pattern), ct);

    /// <summary>Applies every rule to all transactions. Idempotent; returns the number of transactions renamed.</summary>
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var rules = (await store.ListAsync(ct)).OrderByDescending(r => r.Pattern.Length).ToList();
        if (rules.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        foreach (var t in await transactions.LoadTrackedAsync(Max, ct))
        {
            var description = NormalizePattern(t.Description);
            var rule = rules.FirstOrDefault(r => description.Contains(r.Pattern, StringComparison.Ordinal));
            if (rule is not null && t.SetMerchantName(rule.Alias))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await transactions.SaveAsync(ct);
        }

        return changed;
    }
}
