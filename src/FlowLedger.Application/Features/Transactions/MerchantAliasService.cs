using FlowLedger.Application.Abstractions;
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
    private const int Max = TransactionQuota.ProcessingCap;
    public const int MaxLength = 200;
    public const int MaxAliases = 1000;

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
            throw new RequestValidationException("alias", "Both the text to match and the alias are required.");
        }

        if (key.Length > MaxLength || name.Length > MaxLength)
        {
            throw new RequestValidationException("alias", $"Pattern and alias must be {MaxLength} characters or fewer.");
        }

        var existing = await store.ListAsync(ct);
        if (existing.Count >= MaxAliases && !existing.Any(a => string.Equals(a.Pattern, key, StringComparison.Ordinal)))
        {
            throw new RequestValidationException("alias", $"You can have at most {MaxAliases} merchant aliases. Remove one before adding another.");
        }

        await store.SetAsync(key, name, ct);
        return await ApplyAsync(ct);
    }

    /// <summary>
    /// Bank descriptor text -> the service name used in the household bill sheet. Many bills arrive via PayPal/Google Pay,
    /// so the descriptor names the processor ("PP*GOOGLE LLC GOOGLE W") rather than the service ("G-Suite").
    /// </summary>
    public static readonly IReadOnlyList<MerchantAliasDto> SheetDefaults =
    [
        new("GOOGLE LLC GOOGLE W", "G-Suite"),
        new("NABU CASA", "Home Assistant Cloud"),
        new("PAYPAL *MICROSOFT", "Microsoft 365 Family"),
        new("DIGITALOCEA", "Digital Ocean"),
        new("PRUDENTIAL PAYMENTS", "Prudential Life"),
        new("GOODLEAP", "Goodleap"),
        new("ROCKET MORTGAGE", "Rocket Mortgage"),
        new("ATT PAYMENT", "AT&T"),
        new("OK NATURAL GAS", "Oklahoma Natural Gas"),
        new("AMER ELECT PWR", "AEP"),
        new("NORTHWESTERN MY CHART", "Northwestern Medicine"),
        new("ADOBE INC ADOBE", "Adobe CC"),
        new("GITHUB INC", "GitHub"),
        new("OPENAI *CHATGPT", "OpenAI"),
        new("CITY OF TULSA", "City of Tulsa"),
        new("NETFLIX", "Netflix"),
        new("STATE FARM", "State Farm"),
        new("GOOGLE ONE", "Google One"),
        new("INVOICENINJA", "Invoice Ninja"),
        new("JETBRAINS", "JetBrains"),
        new("GOOGLE DISCORD", "Discord Nitro"),
        new("ANTHROPIC", "Claude"),
        new("STELLANTIS", "Stellantis"),
        new("HMFUSA", "HMF Hyundai"),
        new("GOOGLE YOUTUBE", "YouTube Premium Family"),
        new("HUMBLEBUNDL", "Humble Bundle"),
        new("GOOGLE HBO MAX", "HBO Max"),
        new("MUSTANG MOWING", "Mustang Mowing"),
        new("PY *MOXIE", "Moxie Pest Control"),
    ];

    /// <summary>Adds the sheet-derived rules the user does not already have (never overwrites), then applies them. Returns rules added.</summary>
    public async Task<int> SeedSheetDefaultsAsync(CancellationToken ct = default)
    {
        var existing = (await store.ListAsync(ct)).Select(r => r.Pattern).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var rule in SheetDefaults)
        {
            var key = NormalizePattern(rule.Pattern);
            if (existing.Add(key))
            {
                await store.SetAsync(key, rule.Alias, ct);
                added++;
            }
        }

        if (added > 0)
        {
            await ApplyAsync(ct);
        }

        return added;
    }

    public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => store.RemoveAsync(NormalizePattern(pattern), ct);

    /// <summary>Applies every rule to all transactions. Idempotent; returns the number of transactions renamed.</summary>
    public Task<int> ApplyAsync(CancellationToken ct = default) => ApplyToAsync(null, ct);

    /// <summary>Applies every rule to just <paramref name="onlyIds"/> (the rows a sync touched); null means the whole processing window.</summary>
    public async Task<int> ApplyToAsync(IReadOnlyCollection<Guid>? onlyIds, CancellationToken ct = default)
    {
        var rules = (await store.ListAsync(ct)).OrderByDescending(r => r.Pattern.Length).ToList();
        if (rules.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        var changedRows = new List<FlowLedger.Domain.Aggregates.Transaction>();
        foreach (var t in onlyIds is null ? await transactions.LoadAsync(Max, ct) : await transactions.LoadByIdsAsync(onlyIds, ct))
        {
            var description = NormalizePattern(t.Description);
            var rule = rules.FirstOrDefault(r => description.Contains(r.Pattern, StringComparison.Ordinal));
            if (rule is not null && t.SetMerchantName(rule.Alias))
            {
                changedRows.Add(t);
                changed++;
            }
        }

        if (changed > 0)
        {
            await transactions.SaveChangedAsync(changedRows, ct);
        }

        return changed;
    }
}
