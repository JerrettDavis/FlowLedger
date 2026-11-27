using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Assets;

/// <summary>A user-correctable problem with an asset request (unknown feed, unlinkable account…). Maps to HTTP 400.</summary>
public sealed class AssetOperationException(string message) : Exception(message);

internal static class AssetClock
{
    /// <summary>Today (UTC) from the injected <see cref="TimeProvider"/>; the system clock only when none was supplied.</summary>
    public static DateOnly Today(TimeProvider? clock) => DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
}

internal static class AssetMapper
{
    public static AssetDto ToDto(Asset a, DateOnly today, IReadOnlyDictionary<Guid, Account> accounts, bool withHistory, int spreadWindowDays = 0)
    {
        var estimate = AssetValuationEstimator.Estimate(a, today).Amount;
        Account? linked = null;
        if (a.LinkedAccountId is { } id)
        {
            accounts.TryGetValue(id, out linked);
        }

        decimal? liability = null;
        decimal? equity = null;
        if (linked is not null && NetWorthCalculator.IsLiability(linked.AccountType))
        {
            liability = linked.CurrentBalance.Amount;
            equity = estimate - liability;
        }

        return new AssetDto(
            a.Id, a.Name, a.Category.ToString(), a.Manufacturer, a.Model, a.SerialNumber, a.Notes,
            a.PurchaseDate, a.PurchasePrice?.Amount, a.CurrentValue.Currency.Code,
            a.CurrentValue.Amount, a.ValuedOn, estimate,
            a.Method.ToString(), a.AnnualRate, a.SalvageValue, a.UsefulLifeYears,
            a.LinkedAccountId, linked?.Name, liability, equity,
            a.PurchasePrice is null ? null : estimate - a.PurchasePrice.Amount,
            new Dictionary<string, string>(a.Attributes),
            a.IsActive, a.CreatedAt,
            withHistory
                ? a.Valuations.OrderByDescending(v => v.AsOf).ThenByDescending(v => v.RecordedAt)
                    .Select(v => new AssetValuationDto(v.Id, v.Amount, v.AsOf, v.Source, v.Note, v.RecordedAt)).ToList()
                : null,
            a.GetSpread(spreadWindowDays) is { } sp
                ? new ValuationSpreadDto(sp.Low, sp.High, sp.Median, sp.Average, sp.SourceCount, sp.WindowDays)
                : null);
    }

    public static async Task<Dictionary<Guid, Account>> AccountsByIdAsync(IAccountRepository repo, CancellationToken ct)
        => (await repo.ListAsync(ct)).ToDictionary(x => x.Id);

    public static ValuationMethod ParseMethod(string? method)
        => string.IsNullOrWhiteSpace(method) ? ValuationMethod.Manual : Enum.Parse<ValuationMethod>(method, ignoreCase: true);

    /// <summary>
    /// Resolves the link target via the tenant-filtered account repository. Unknown and other-tenant
    /// accounts are indistinguishable (both "not found") so ids cannot be probed across tenants.
    /// </summary>
    public static async Task<Account?> ResolveLinkAsync(IAccountRepository repo, Guid? accountId, CancellationToken ct)
    {
        if (accountId is not { } id)
        {
            return null;
        }

        var account = await repo.GetByIdAsync(AccountId.From(id), ct);
        return account is { IsActive: true }
            ? account
            : throw new AssetOperationException("The linked account was not found.");
    }

    /// <summary>
    /// A ManualAsset account linked to assets carries their combined value into net worth. After ANY change
    /// to an asset or its link, recompute each affected account as the sum of its active linked assets
    /// (0 when none remain) so an unlink, relink, delete or shared link can never leave a stale balance
    /// that would double count or under count.
    /// </summary>
    /// <param name="inFlight">The asset being changed. Its in-memory state wins over the database list,
    /// which can omit a not-yet-saved asset or still include one just deactivated.</param>
    public static async Task SyncManualAccountsAsync(
        IAssetRepository assets, IAccountRepository accounts, IEnumerable<Guid?> accountIds, Asset inFlight, CancellationToken ct, TimeProvider? clock = null)
    {
        var ids = accountIds.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var all = (await assets.ListAsync(ct)).Where(a => a.Id != inFlight.Id).Append(inFlight).Where(a => a.IsActive).ToList();
        foreach (var id in ids)
        {
            var account = await accounts.GetByIdAsync(AccountId.From(id), ct);
            if (account is not { AccountType: AccountType.ManualAsset, IsActive: true })
            {
                continue;
            }

            var total = all
                .Where(a => a.LinkedAccountId == id && a.CurrentValue.Currency == account.CurrentBalance.Currency)
                .Sum(a => a.CurrentValue.Amount);
            if (total != account.CurrentBalance.Amount)
            {
                account.UpdateBalance(new Money(total, account.CurrentBalance.Currency), clock);
            }
        }
    }
}

public sealed class ListAssetsHandler(IAssetRepository assets, IAccountRepository accounts, TimeProvider? clock = null)
{
    public async Task<IReadOnlyList<AssetDto>> HandleAsync(CancellationToken ct = default)
    {
        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        var today = AssetClock.Today(clock);
        return (await assets.ListAsync(ct)).Select(a => AssetMapper.ToDto(a, today, map, withHistory: false)).ToList();
    }
}

public sealed class GetAssetHandler(IAssetRepository assets, IAccountRepository accounts, AssetValuationSettings settings, TimeProvider? clock = null)
{
    public async Task<AssetDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        return asset is null
            ? null
            : AssetMapper.ToDto(asset, AssetClock.Today(clock), await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true, spreadWindowDays: settings.BlendWindowDays);
    }
}

public sealed class CreateAssetHandler(IAssetRepository assets, IAccountRepository accounts, ITenantContext tenant, TimeProvider? clock = null)
{
    public async Task<AssetDto> HandleAsync(CreateAssetRequest r, CancellationToken ct = default)
    {
        FlowLedger.Application.Abstractions.TenantLimits.EnsureRoom((await assets.ListAsync(ct)).Count, 1, FlowLedger.Application.Abstractions.TenantLimits.MaxAssets, "assets");
        var currency = new Currency(r.Currency);
        var linked = await AssetMapper.ResolveLinkAsync(accounts, r.LinkedAccountId, ct);
        var category = Enum.Parse<AssetCategory>(r.Category, ignoreCase: true);
        var valuedOn = r.ValuedOn ?? AssetClock.Today(clock);

        var asset = Asset.Create(TenantId.From(tenant.TenantId), r.Name, category, new Money(r.CurrentValue, currency), valuedOn, time: clock);
        asset.UpdateDetails(r.Name, category, r.Manufacturer, r.Model, r.SerialNumber, r.Notes, r.PurchaseDate,
            r.PurchasePrice is { } p ? new Money(p, currency) : null, r.Attributes);
        asset.SetValuationModel(AssetMapper.ParseMethod(r.ValuationMethod), r.AnnualRate ?? 0m, r.SalvageValue, r.UsefulLifeYears);
        asset.LinkAccount(linked);

        await assets.AddAsync(asset, ct);
        await AssetMapper.SyncManualAccountsAsync(assets, accounts, [asset.LinkedAccountId], asset, ct, clock);
        await assets.SaveChangesAsync(ct);

        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        return AssetMapper.ToDto(asset, AssetClock.Today(clock), map, withHistory: true);
    }
}

public sealed class UpdateAssetHandler(IAssetRepository assets, IAccountRepository accounts, TimeProvider? clock = null)
{
    public async Task<AssetDto?> HandleAsync(Guid id, UpdateAssetRequest r, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        var linked = await AssetMapper.ResolveLinkAsync(accounts, r.LinkedAccountId, ct);
        var previousLink = asset.LinkedAccountId;
        var currency = asset.CurrentValue.Currency;

        asset.UpdateDetails(r.Name, Enum.Parse<AssetCategory>(r.Category, ignoreCase: true), r.Manufacturer, r.Model,
            r.SerialNumber, r.Notes, r.PurchaseDate, r.PurchasePrice is { } p ? new Money(p, currency) : null, r.Attributes);
        asset.SetValuationModel(AssetMapper.ParseMethod(r.ValuationMethod), r.AnnualRate ?? 0m, r.SalvageValue, r.UsefulLifeYears);
        asset.LinkAccount(linked);
        await AssetMapper.SyncManualAccountsAsync(assets, accounts, [previousLink, asset.LinkedAccountId], asset, ct, clock);

        await assets.SaveChangesAsync(ct);
        return AssetMapper.ToDto(asset, AssetClock.Today(clock), await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true);
    }
}

/// <summary>Records a new valuation (e.g. an appraisal) and keeps a linked ManualAsset account in step.</summary>
public sealed class RecordAssetValuationHandler(IAssetRepository assets, IAccountRepository accounts, AssetValuationSettings settings, TimeProvider? clock = null)
{
    public async Task<AssetDto?> HandleAsync(Guid id, RecordValuationRequest r, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        asset.RecordValuation(
            new Money(r.Amount, asset.CurrentValue.Currency), r.AsOf ?? AssetClock.Today(clock),
            string.IsNullOrWhiteSpace(r.Source) ? AssetValuationSources.Manual : r.Source, r.Note, settings.BlendWindowDays, clock);
        await AssetMapper.SyncManualAccountsAsync(assets, accounts, [asset.LinkedAccountId], asset, ct, clock);

        await assets.SaveChangesAsync(ct);
        return AssetMapper.ToDto(asset, AssetClock.Today(clock), await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true, spreadWindowDays: settings.BlendWindowDays);
    }
}

public sealed class DeactivateAssetHandler(IAssetRepository assets, IAccountRepository accounts, TimeProvider? clock = null)
{
    public async Task<bool> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return false;
        }

        asset.Deactivate();
        await AssetMapper.SyncManualAccountsAsync(assets, accounts, [asset.LinkedAccountId], asset, ct, clock);
        await assets.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Dashboard overview: holdings totals, gain/loss, equity, category breakdown and net worth.</summary>
public sealed class GetAssetSummaryHandler(IAssetRepository assets, IAccountRepository accounts, TimeProvider? clock = null)
{
    public async Task<AssetSummaryDto> HandleAsync(CancellationToken ct = default)
    {
        var today = AssetClock.Today(clock);
        var accountList = await accounts.ListAsync(ct);
        var map = accountList.ToDictionary(a => a.Id);
        var domainAssets = await assets.ListAsync(ct);
        var list = domainAssets.Select(a => AssetMapper.ToDto(a, today, map, withHistory: false)).ToList();

        var withCost = list.Where(a => a.PurchasePrice.HasValue).ToList();
        var byCategory = list.GroupBy(a => a.Category)
            .Select(g => new AssetCategoryBreakdownDto(g.Key, g.Count(), g.Sum(a => a.EstimatedValue)))
            .OrderByDescending(c => c.Value).ToList();
        // Several assets may share one mortgage; count each linked liability account once.
        var liabilities = list.Where(a => a.LinkedLiability.HasValue && a.LinkedAccountId.HasValue)
            .GroupBy(a => a.LinkedAccountId!.Value).Sum(g => g.First().LinkedLiability!.Value);
        var totalValue = list.Sum(a => a.EstimatedValue);

        var net = NetWorthCalculator.Compute(accountList, domainAssets, today);

        return new AssetSummaryDto(
            list.Count,
            totalValue,
            withCost.Sum(a => a.PurchasePrice!.Value),
            withCost.Sum(a => a.GainLoss ?? 0m),
            liabilities,
            totalValue - liabilities,
            byCategory,
            list.OrderByDescending(a => a.EstimatedValue).Take(5).ToList(),
            net);
    }
}

/// <summary>Asks a valuation feed for a fresh quote and records it as a new valuation.</summary>
public sealed class RefreshAssetValuationHandler(
    IAssetRepository assets,
    IAccountRepository accounts,
    IEnumerable<IAssetValuationFeed> feeds,
    AssetValuationSettings settings,
    TimeProvider? clock = null)
{
    public async Task<AssetRefreshResultDto?> HandleAsync(Guid id, string? feedName, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        var feed = ResolveFeed(feedName);
        var today = AssetClock.Today(clock);
        var quote = await feed.GetQuoteAsync(asset, today, ct);
        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        if (quote is null)
        {
            return new AssetRefreshResultDto(false, $"Feed '{feed.Name}' has no value for this asset.",
                AssetMapper.ToDto(asset, today, map, withHistory: true, spreadWindowDays: settings.BlendWindowDays));
        }

        asset.RecordValuation(new Money(quote.Amount, asset.CurrentValue.Currency), quote.AsOf, feed.Name, quote.Note, settings.BlendWindowDays, clock);
        await AssetMapper.SyncManualAccountsAsync(assets, accounts, [asset.LinkedAccountId], asset, ct, clock);
        await assets.SaveChangesAsync(ct);
        map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        return new AssetRefreshResultDto(true, $"Updated from '{feed.Name}'.",
            AssetMapper.ToDto(asset, today, map, withHistory: true, spreadWindowDays: settings.BlendWindowDays));
    }

    private IAssetValuationFeed ResolveFeed(string? name)
    {
        var wanted = string.IsNullOrWhiteSpace(name) ? ModelEstimateFeed.FeedName : name.Trim();
        var feed = feeds.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? throw new AssetOperationException($"Unknown valuation feed '{wanted}'.");
        return feed.IsEnabled ? feed : throw new AssetOperationException($"Valuation feed '{feed.Name}' is not enabled.");
    }
}

/// <summary>
/// Valuation behaviour settings. <see cref="BlendWindowDays"/>: valuations from different sources dated within
/// +/- this many days are averaged into the current value (0 disables blending). Bound from
/// <c>AssetValuation:BlendWindowDays</c>.
/// </summary>
public sealed class AssetValuationSettings
{
    public int BlendWindowDays { get; init; } = 7;
}
