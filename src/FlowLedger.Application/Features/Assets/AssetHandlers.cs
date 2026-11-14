using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Assets;

/// <summary>A user-correctable problem with an asset request (unknown feed, unlinkable account…). Maps to HTTP 400.</summary>
public sealed class AssetOperationException(string message) : Exception(message);

internal static class AssetClock
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}

internal static class AssetMapper
{
    public static AssetDto ToDto(Asset a, DateOnly today, IReadOnlyDictionary<Guid, Account> accounts, bool withHistory)
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
            liability = Math.Abs(linked.CurrentBalance.Amount);
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

    /// <summary>A linked ManualAsset account carries the asset's value into net worth, so keep it in step.</summary>
    public static void PushValueToLinkedAccount(Asset asset, Account? linked)
    {
        if (linked is { AccountType: AccountType.ManualAsset })
        {
            linked.UpdateBalance(new Money(asset.CurrentValue.Amount, linked.CurrentBalance.Currency));
        }
    }
}

public sealed class ListAssetsHandler(IAssetRepository assets, IAccountRepository accounts)
{
    public async Task<IReadOnlyList<AssetDto>> HandleAsync(CancellationToken ct = default)
    {
        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        var today = AssetClock.Today;
        return (await assets.ListAsync(ct)).Select(a => AssetMapper.ToDto(a, today, map, withHistory: false)).ToList();
    }
}

public sealed class GetAssetHandler(IAssetRepository assets, IAccountRepository accounts)
{
    public async Task<AssetDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        return asset is null
            ? null
            : AssetMapper.ToDto(asset, AssetClock.Today, await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true);
    }
}

public sealed class CreateAssetHandler(IAssetRepository assets, IAccountRepository accounts, ITenantContext tenant)
{
    public async Task<AssetDto> HandleAsync(CreateAssetRequest r, CancellationToken ct = default)
    {
        var currency = new Currency(r.Currency);
        var linked = await AssetMapper.ResolveLinkAsync(accounts, r.LinkedAccountId, ct);
        var category = Enum.Parse<AssetCategory>(r.Category, ignoreCase: true);
        var valuedOn = r.ValuedOn ?? AssetClock.Today;

        var asset = Asset.Create(TenantId.From(tenant.TenantId), r.Name, category, new Money(r.CurrentValue, currency), valuedOn);
        asset.UpdateDetails(r.Name, category, r.Manufacturer, r.Model, r.SerialNumber, r.Notes, r.PurchaseDate,
            r.PurchasePrice is { } p ? new Money(p, currency) : null, r.Attributes);
        asset.SetValuationModel(AssetMapper.ParseMethod(r.ValuationMethod), r.AnnualRate ?? 0m, r.SalvageValue, r.UsefulLifeYears);
        asset.LinkAccount(linked);
        AssetMapper.PushValueToLinkedAccount(asset, linked);

        await assets.AddAsync(asset, ct);
        await assets.SaveChangesAsync(ct);

        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        return AssetMapper.ToDto(asset, AssetClock.Today, map, withHistory: true);
    }
}

public sealed class UpdateAssetHandler(IAssetRepository assets, IAccountRepository accounts)
{
    public async Task<AssetDto?> HandleAsync(Guid id, UpdateAssetRequest r, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        var linked = await AssetMapper.ResolveLinkAsync(accounts, r.LinkedAccountId, ct);
        var currency = asset.CurrentValue.Currency;

        asset.UpdateDetails(r.Name, Enum.Parse<AssetCategory>(r.Category, ignoreCase: true), r.Manufacturer, r.Model,
            r.SerialNumber, r.Notes, r.PurchaseDate, r.PurchasePrice is { } p ? new Money(p, currency) : null, r.Attributes);
        asset.SetValuationModel(AssetMapper.ParseMethod(r.ValuationMethod), r.AnnualRate ?? 0m, r.SalvageValue, r.UsefulLifeYears);
        asset.LinkAccount(linked);
        AssetMapper.PushValueToLinkedAccount(asset, linked);

        await assets.SaveChangesAsync(ct);
        return AssetMapper.ToDto(asset, AssetClock.Today, await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true);
    }
}

/// <summary>Records a new valuation (e.g. an appraisal) and keeps a linked ManualAsset account in step.</summary>
public sealed class RecordAssetValuationHandler(IAssetRepository assets, IAccountRepository accounts)
{
    public async Task<AssetDto?> HandleAsync(Guid id, RecordValuationRequest r, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        asset.RecordValuation(
            new Money(r.Amount, asset.CurrentValue.Currency), r.AsOf ?? AssetClock.Today,
            string.IsNullOrWhiteSpace(r.Source) ? AssetValuationSources.Manual : r.Source, r.Note);
        await ApplyLinkedAccountAsync(asset, accounts, ct);

        await assets.SaveChangesAsync(ct);
        return AssetMapper.ToDto(asset, AssetClock.Today, await AssetMapper.AccountsByIdAsync(accounts, ct), withHistory: true);
    }

    internal static async Task ApplyLinkedAccountAsync(Asset asset, IAccountRepository accounts, CancellationToken ct)
    {
        if (asset.LinkedAccountId is { } linkId)
        {
            AssetMapper.PushValueToLinkedAccount(asset, await accounts.GetByIdAsync(AccountId.From(linkId), ct));
        }
    }
}

public sealed class DeactivateAssetHandler(IAssetRepository assets)
{
    public async Task<bool> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return false;
        }

        asset.Deactivate();
        await assets.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Dashboard overview: holdings totals, gain/loss, equity, category breakdown and net worth.</summary>
public sealed class GetAssetSummaryHandler(IAssetRepository assets, IAccountRepository accounts)
{
    public async Task<AssetSummaryDto> HandleAsync(CancellationToken ct = default)
    {
        var today = AssetClock.Today;
        var accountList = await accounts.ListAsync(ct);
        var map = accountList.ToDictionary(a => a.Id);
        var domainAssets = await assets.ListAsync(ct);
        var list = domainAssets.Select(a => AssetMapper.ToDto(a, today, map, withHistory: false)).ToList();

        var withCost = list.Where(a => a.PurchasePrice.HasValue).ToList();
        var byCategory = list.GroupBy(a => a.Category)
            .Select(g => new AssetCategoryBreakdownDto(g.Key, g.Count(), g.Sum(a => a.EstimatedValue)))
            .OrderByDescending(c => c.Value).ToList();
        var liabilities = list.Sum(a => a.LinkedLiability ?? 0m);
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
    IEnumerable<IAssetValuationFeed> feeds)
{
    public async Task<AssetRefreshResultDto?> HandleAsync(Guid id, string? feedName, CancellationToken ct = default)
    {
        var asset = await assets.GetByIdAsync(AssetId.From(id), ct);
        if (asset is null)
        {
            return null;
        }

        var feed = ResolveFeed(feedName);
        var today = AssetClock.Today;
        var quote = await feed.GetQuoteAsync(asset, today, ct);
        var map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        if (quote is null)
        {
            return new AssetRefreshResultDto(false, $"Feed '{feed.Name}' has no value for this asset.",
                AssetMapper.ToDto(asset, today, map, withHistory: true));
        }

        asset.RecordValuation(new Money(quote.Amount, asset.CurrentValue.Currency), quote.AsOf, feed.Name, quote.Note);
        await RecordAssetValuationHandler.ApplyLinkedAccountAsync(asset, accounts, ct);
        await assets.SaveChangesAsync(ct);
        map = await AssetMapper.AccountsByIdAsync(accounts, ct);
        return new AssetRefreshResultDto(true, $"Updated from '{feed.Name}'.",
            AssetMapper.ToDto(asset, today, map, withHistory: true));
    }

    private IAssetValuationFeed ResolveFeed(string? name)
    {
        var wanted = string.IsNullOrWhiteSpace(name) ? ModelEstimateFeed.FeedName : name.Trim();
        var feed = feeds.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? throw new AssetOperationException($"Unknown valuation feed '{wanted}'.");
        return feed.IsEnabled ? feed : throw new AssetOperationException($"Valuation feed '{feed.Name}' is not enabled.");
    }
}
