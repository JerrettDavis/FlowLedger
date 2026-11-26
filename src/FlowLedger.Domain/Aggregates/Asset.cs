using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// A physical or tangible item of value owned by a tenant (vehicle, electronics, firearm, property…).
/// Tracks its purchase, a valuation model (depreciation / appreciation) and an append-only valuation
/// history. May be linked to an <see cref="Account"/> — a Mortgage/Loan financing it, or a
/// ManualAsset account that carries its value into net worth.
///
/// <see cref="CurrentValue"/> is always the most recent <em>recorded</em> valuation (the anchor).
/// <see cref="AssetValuationEstimator"/> projects it forward to any date using the valuation model.
/// </summary>
public sealed class Asset : AggregateRoot
{
    public const int MaxAttributes = 25;
    private readonly List<AssetValuation> _valuations = [];

    public override Guid Id { get; }
    public AssetId AssetId => AssetId.From(Id);
    public TenantId TenantId { get; }
    public string Name { get; private set; }
    public AssetCategory Category { get; private set; }
    public string? Manufacturer { get; private set; }
    public string? Model { get; private set; }

    /// <summary>Sensitive (e.g. firearm / device serial). Never log or send to external feeds by default.</summary>
    public string? SerialNumber { get; private set; }
    public string? Notes { get; private set; }
    public DateOnly? PurchaseDate { get; private set; }
    public Money? PurchasePrice { get; private set; }
    public Money CurrentValue { get; private set; }
    public DateOnly ValuedOn { get; private set; }
    public ValuationMethod Method { get; private set; }

    /// <summary>Annual rate as a fraction (0.15 = 15%). Decline rate for DecliningBalance, growth for Appreciation.</summary>
    public decimal AnnualRate { get; private set; }
    public decimal? SalvageValue { get; private set; }
    public int? UsefulLifeYears { get; private set; }

    /// <summary>Optional linked account (financing liability, or a ManualAsset account carrying the value).</summary>
    public Guid? LinkedAccountId { get; private set; }

    /// <summary>User-defined and category-suggested extra fields (caliber, VIN, mileage…).</summary>
    public Dictionary<string, string> Attributes { get; private set; } = new();
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<AssetValuation> Valuations => _valuations.AsReadOnly();

    private Asset()
    {
        // EF Core constructor.
        Name = null!;
        CurrentValue = null!;
    }

    private Asset(TenantId tenantId, string name, AssetCategory category, Money value, DateOnly valuedOn, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Name = name;
        Category = category;
        CurrentValue = value;
        ValuedOn = valuedOn;
        CreatedAt = createdAt;
    }

    public static Asset Create(
        TenantId tenantId,
        string name,
        AssetCategory category,
        Money currentValue,
        DateOnly valuedOn,
        string source = AssetValuationSources.Manual)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EmptyStringException(nameof(name));
        }

        GuardNotNegative(currentValue, "current value");

        var asset = new Asset(tenantId, name.Trim(), category, currentValue, valuedOn, DateTimeOffset.UtcNow);
        asset._valuations.Add(AssetValuation.Create(currentValue.Amount, valuedOn, source, "Initial value"));
        asset.RaiseEvent(new AssetAdded(asset.AssetId, tenantId, category));
        return asset;
    }

    public void UpdateDetails(
        string name,
        AssetCategory category,
        string? manufacturer,
        string? model,
        string? serialNumber,
        string? notes,
        DateOnly? purchaseDate,
        Money? purchasePrice,
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EmptyStringException(nameof(name));
        }

        if (purchasePrice is not null)
        {
            GuardNotNegative(purchasePrice, "purchase price");
            if (purchasePrice.Currency != CurrentValue.Currency)
            {
                throw new CurrencyMismatchException(CurrentValue.Currency.Code, purchasePrice.Currency.Code);
            }
        }

        Name = name.Trim();
        Category = category;
        Manufacturer = Clean(manufacturer);
        Model = Clean(model);
        SerialNumber = Clean(serialNumber);
        Notes = Clean(notes);
        PurchaseDate = purchaseDate;
        PurchasePrice = purchasePrice;
        Attributes = CleanAttributes(attributes);
    }

    public void SetValuationModel(ValuationMethod method, decimal annualRate, decimal? salvageValue, int? usefulLifeYears)
    {
        if (annualRate is < 0m or > 1m)
        {
            throw new InvalidAssetException("Annual rate must be between 0 and 1 (e.g. 0.15 for 15%).");
        }

        if (salvageValue is < 0m)
        {
            throw new InvalidAssetException("Salvage value cannot be negative.");
        }

        if (usefulLifeYears is <= 0)
        {
            throw new InvalidAssetException("Useful life must be a positive number of years.");
        }

        if (method == ValuationMethod.StraightLine && usefulLifeYears is null)
        {
            throw new InvalidAssetException("Straight-line depreciation requires a useful life in years.");
        }

        Method = method;
        AnnualRate = annualRate;
        SalvageValue = salvageValue;
        UsefulLifeYears = usefulLifeYears;
    }

    /// <summary>
    /// Appends a valuation to the history. If it is dated on/after the current anchor it also becomes
    /// the current value; an older back-dated valuation only enriches history.
    /// </summary>
    /// <param name="blendWindowDays">
    /// When greater than zero, competing valuations (different sources) dated within this many days of the
    /// newest valuation are averaged into <see cref="CurrentValue"/> instead of the latest one winning.
    /// </param>
    public AssetValuation RecordValuation(Money value, DateOnly asOf, string source, string? note = null, int blendWindowDays = 0)
    {
        if (value.Currency != CurrentValue.Currency)
        {
            throw new CurrencyMismatchException(CurrentValue.Currency.Code, value.Currency.Code);
        }

        GuardNotNegative(value, "valuation");

        var valuation = AssetValuation.Create(value.Amount, asOf, source, note);
        _valuations.Add(valuation);

        if (asOf >= ValuedOn)
        {
            var previous = CurrentValue;
            var blended = blendWindowDays > 0 ? BlendedValue(asOf, blendWindowDays, value.Currency) : value;
            CurrentValue = blended;
            ValuedOn = asOf;
            RaiseEvent(new AssetRevalued(AssetId, TenantId, previous, blended));
        }

        return valuation;
    }

    /// <summary>
    /// Average of the latest valuation from each distinct source dated within the window ending at
    /// <paramref name="anchor"/>. One value per source so a frequently-polled feed doesn't outweigh the others.
    /// </summary>
    private Money BlendedValue(DateOnly anchor, int windowDays, Currency currency)
    {
        var perSource = LatestPerSource(anchor, windowDays);
        return new Money(MoneyRounding.Round(perSource.Average(v => v.Amount)), currency);
    }

    private List<AssetValuation> LatestPerSource(DateOnly anchor, int windowDays)
    {
        var from = anchor.AddDays(-windowDays);
        return _valuations
            .Where(v => v.AsOf >= from && v.AsOf <= anchor)
            .GroupBy(v => v.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(v => v.AsOf).ThenByDescending(v => v.RecordedAt).First())
            .ToList();
    }

    /// <summary>
    /// Low / high / median / average across the latest valuation per source within the window ending at
    /// <see cref="ValuedOn"/> (the same set <see cref="CurrentValue"/> is blended from). Null when the window is off.
    /// </summary>
    public ValuationSpread? GetSpread(int windowDays)
    {
        if (windowDays <= 0)
        {
            return null;
        }

        var amounts = LatestPerSource(ValuedOn, windowDays).Select(v => v.Amount).Order().ToList();
        if (amounts.Count == 0)
        {
            return null;
        }

        var mid = amounts.Count / 2;
        var median = amounts.Count % 2 == 1 ? amounts[mid] : (amounts[mid - 1] + amounts[mid]) / 2m;
        return new ValuationSpread(
            amounts[0], amounts[^1], MoneyRounding.Round(median),
            MoneyRounding.Round(amounts.Average()), amounts.Count, windowDays);
    }

    /// <summary>Links (or, with null, unlinks) an account. The account must belong to the same tenant.</summary>
    public void LinkAccount(Account? account)
    {
        if (account is not null && account.TenantId != TenantId)
        {
            throw new InvalidAssetException("Cannot link an account from a different tenant.");
        }

        LinkedAccountId = account?.Id;
    }

    public void Deactivate() => IsActive = false;

    private static void GuardNotNegative(Money money, string what)
    {
        if (money.IsNegative)
        {
            throw new InvalidAssetException($"Asset {what} cannot be negative.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string> CleanAttributes(IReadOnlyDictionary<string, string>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attributes is null)
        {
            return result;
        }

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key.Trim()] = value.Trim();
        }

        if (result.Count > MaxAttributes)
        {
            throw new InvalidAssetException($"An asset can have at most {MaxAttributes} custom fields.");
        }

        return result;
    }
}

/// <summary>One point in an asset's append-only valuation history. Owned by <see cref="Asset"/>.</summary>
/// <summary>Spread of competing valuations inside the blend window.</summary>
public sealed record ValuationSpread(decimal Low, decimal High, decimal Median, decimal Average, int SourceCount, int WindowDays);

public sealed class AssetValuation
{
    public Guid Id { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly AsOf { get; private set; }
    public string Source { get; private set; } = AssetValuationSources.Manual;
    public string? Note { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    private AssetValuation() { }

    internal static AssetValuation Create(decimal amount, DateOnly asOf, string source, string? note) => new()
    {
        Id = Guid.NewGuid(),
        Amount = amount,
        AsOf = asOf,
        Source = string.IsNullOrWhiteSpace(source) ? AssetValuationSources.Manual : source.Trim(),
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        RecordedAt = DateTimeOffset.UtcNow,
    };
}

public static class AssetValuationSources
{
    public const string Manual = "Manual";
    public const string Estimate = "Estimate";
}

public enum AssetCategory
{
    RealEstate,
    Vehicle,
    Electronics,
    Firearm,
    Jewelry,
    Collectible,
    Art,
    Furniture,
    Appliance,
    Tool,
    Investment,
    Other,
}

public enum ValuationMethod
{
    /// <summary>Value only changes when someone records a new valuation.</summary>
    Manual,

    /// <summary>Loses a fixed amount per year down to <see cref="Asset.SalvageValue"/> over its useful life.</summary>
    StraightLine,

    /// <summary>Loses <see cref="Asset.AnnualRate"/> of its value each year (compounding).</summary>
    DecliningBalance,

    /// <summary>Gains <see cref="Asset.AnnualRate"/> each year (compounding).</summary>
    Appreciation,
}
