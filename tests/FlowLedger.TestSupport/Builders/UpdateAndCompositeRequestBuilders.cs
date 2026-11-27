using FlowLedger.Application.Features.Budgets;

namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="UpdateAccountRequest"/>: a rename to "Renamed".</summary>
public sealed class UpdateAccountRequestBuilder
{
    private string _name = "Renamed";

    public static UpdateAccountRequestBuilder A() => new();

    public UpdateAccountRequestBuilder Named(string name) { _name = name; return this; }

    public UpdateAccountRequest Build() => new(_name);

    public static implicit operator UpdateAccountRequest(UpdateAccountRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="UpdateCategoryRequest"/>: path "category", display name equal to the path unless named.</summary>
public sealed class UpdateCategoryRequestBuilder
{
    private string _path = "category";
    private string? _displayName;

    public static UpdateCategoryRequestBuilder A() => new();

    public UpdateCategoryRequestBuilder AtPath(string path) { _path = path; return this; }
    public UpdateCategoryRequestBuilder Named(string displayName) { _displayName = displayName; return this; }

    public UpdateCategoryRequest Build() => new(_path, _displayName ?? _path);

    public static implicit operator UpdateCategoryRequest(UpdateCategoryRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="UpdateRecurringFlowRequest"/>: 1500.00, Fixed amount model.</summary>
public sealed class UpdateRecurringFlowRequestBuilder
{
    private decimal _amount = 1500m;
    private string _amountModel = "Fixed";

    public static UpdateRecurringFlowRequestBuilder A() => new();

    public UpdateRecurringFlowRequestBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public UpdateRecurringFlowRequestBuilder WithModel(string amountModel) { _amountModel = amountModel; return this; }

    public UpdateRecurringFlowRequest Build() => new(_amount, _amountModel);

    public static implicit operator UpdateRecurringFlowRequest(UpdateRecurringFlowRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CreateRecurringFlowRequest"/>: monthly Fixed 1200.00 USD Debit "Rent" on the 1st, starting 2026-01-01, open ended.</summary>
public sealed class CreateRecurringFlowRequestBuilder
{
    private Guid _accountId = Guid.NewGuid();
    private string _name = "Rent";
    private decimal _amount = 1200m;
    private string _currency = "USD";
    private string _direction = "Debit";
    private string _amountModel = "Fixed";
    private string _frequency = "Monthly";
    private int? _dayOfMonth = 1;
    private int? _secondDayOfMonth;
    private int? _intervalWeeks;
    private string? _anchorDayOfWeek;
    private DateOnly _startDate = new(2026, 1, 1);
    private DateOnly? _endDate;
    private Guid? _categoryId;
    private string? _counterparty;

    public static CreateRecurringFlowRequestBuilder A() => new();

    public CreateRecurringFlowRequestBuilder OnAccount(Guid accountId) { _accountId = accountId; return this; }
    public CreateRecurringFlowRequestBuilder Named(string name) { _name = name; return this; }
    public CreateRecurringFlowRequestBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public CreateRecurringFlowRequestBuilder InCurrency(string currency) { _currency = currency; return this; }
    public CreateRecurringFlowRequestBuilder WithDirection(string direction) { _direction = direction; return this; }
    public CreateRecurringFlowRequestBuilder WithModel(string amountModel) { _amountModel = amountModel; return this; }
    public CreateRecurringFlowRequestBuilder Every(string frequency, int? dayOfMonth = 1, int? secondDayOfMonth = null, int? intervalWeeks = null, string? anchorDayOfWeek = null)
    {
        _frequency = frequency;
        _dayOfMonth = dayOfMonth;
        _secondDayOfMonth = secondDayOfMonth;
        _intervalWeeks = intervalWeeks;
        _anchorDayOfWeek = anchorDayOfWeek;
        return this;
    }

    public CreateRecurringFlowRequestBuilder From(DateOnly start, DateOnly? end = null) { _startDate = start; _endDate = end; return this; }
    public CreateRecurringFlowRequestBuilder InCategory(Guid? categoryId) { _categoryId = categoryId; return this; }
    public CreateRecurringFlowRequestBuilder WithCounterparty(string? counterparty) { _counterparty = counterparty; return this; }

    public CreateRecurringFlowRequest Build() => new(
        _accountId, _name, _amount, _currency, _direction, _amountModel, _frequency, _dayOfMonth, _secondDayOfMonth, _intervalWeeks,
        _anchorDayOfWeek, _startDate, _endDate, _categoryId, _counterparty);

    public static implicit operator CreateRecurringFlowRequest(CreateRecurringFlowRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CreateAssetRequest"/>: a 2000.00 USD "MacBook" (Electronics) bought 2024-01-01 for 2400.00, Manual valuation.</summary>
public sealed class CreateAssetRequestBuilder
{
    private string _name = "MacBook";
    private string _category = "Electronics";
    private decimal _currentValue = 2000m;
    private string _currency = "USD";
    private DateOnly? _valuedOn;
    private DateOnly? _purchaseDate = new(2024, 1, 1);
    private decimal? _purchasePrice = 2400m;
    private string? _manufacturer = "Apple";
    private string? _model = "MBP";
    private string? _serialNumber;
    private string? _notes;
    private string? _valuationMethod = "Manual";
    private decimal? _annualRate;
    private decimal? _salvageValue;
    private int? _usefulLifeYears;
    private Guid? _linkedAccountId;
    private Dictionary<string, string>? _attributes;

    public static CreateAssetRequestBuilder A() => new();

    public CreateAssetRequestBuilder Named(string name) { _name = name; return this; }
    public CreateAssetRequestBuilder InCategory(string category) { _category = category; return this; }
    public CreateAssetRequestBuilder Worth(decimal currentValue) { _currentValue = currentValue; return this; }
    public CreateAssetRequestBuilder InCurrency(string currency) { _currency = currency; return this; }
    public CreateAssetRequestBuilder ValuedOn(DateOnly? date) { _valuedOn = date; return this; }
    public CreateAssetRequestBuilder PurchasedOn(DateOnly? date, decimal? price) { _purchaseDate = date; _purchasePrice = price; return this; }
    public CreateAssetRequestBuilder MadeBy(string? manufacturer, string? model) { _manufacturer = manufacturer; _model = model; return this; }
    public CreateAssetRequestBuilder WithSerial(string? serialNumber) { _serialNumber = serialNumber; return this; }
    public CreateAssetRequestBuilder WithNotes(string? notes) { _notes = notes; return this; }
    public CreateAssetRequestBuilder Valued(string? method, decimal? annualRate = null, decimal? salvageValue = null, int? usefulLifeYears = null)
    {
        _valuationMethod = method;
        _annualRate = annualRate;
        _salvageValue = salvageValue;
        _usefulLifeYears = usefulLifeYears;
        return this;
    }

    public CreateAssetRequestBuilder LinkedTo(Guid? accountId) { _linkedAccountId = accountId; return this; }
    public CreateAssetRequestBuilder WithAttributes(Dictionary<string, string>? attributes) { _attributes = attributes; return this; }

    public CreateAssetRequest Build() => new(
        _name, _category, _currentValue, _currency, _valuedOn, _purchaseDate, _purchasePrice, _manufacturer, _model, _serialNumber, _notes,
        _valuationMethod, _annualRate, _salvageValue, _usefulLifeYears, _linkedAccountId, _attributes);

    public static implicit operator CreateAssetRequest(CreateAssetRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="UpdateAssetRequest"/>: the same "MacBook" as <see cref="CreateAssetRequestBuilder"/>, nothing changed.</summary>
public sealed class UpdateAssetRequestBuilder
{
    private string _name = "MacBook";
    private string _category = "Electronics";
    private DateOnly? _purchaseDate;
    private decimal? _purchasePrice;
    private string? _manufacturer;
    private string? _model;
    private string? _serialNumber;
    private string? _notes;
    private string? _valuationMethod = "Manual";
    private decimal? _annualRate;
    private decimal? _salvageValue;
    private int? _usefulLifeYears;
    private Guid? _linkedAccountId;
    private Dictionary<string, string>? _attributes;

    public static UpdateAssetRequestBuilder A() => new();

    public UpdateAssetRequestBuilder Named(string name) { _name = name; return this; }
    public UpdateAssetRequestBuilder InCategory(string category) { _category = category; return this; }
    public UpdateAssetRequestBuilder PurchasedOn(DateOnly? date, decimal? price) { _purchaseDate = date; _purchasePrice = price; return this; }
    public UpdateAssetRequestBuilder MadeBy(string? manufacturer, string? model) { _manufacturer = manufacturer; _model = model; return this; }
    public UpdateAssetRequestBuilder WithSerial(string? serialNumber) { _serialNumber = serialNumber; return this; }
    public UpdateAssetRequestBuilder WithNotes(string? notes) { _notes = notes; return this; }
    public UpdateAssetRequestBuilder Valued(string? method, decimal? annualRate = null, decimal? salvageValue = null, int? usefulLifeYears = null)
    {
        _valuationMethod = method;
        _annualRate = annualRate;
        _salvageValue = salvageValue;
        _usefulLifeYears = usefulLifeYears;
        return this;
    }

    public UpdateAssetRequestBuilder LinkedTo(Guid? accountId) { _linkedAccountId = accountId; return this; }
    public UpdateAssetRequestBuilder WithAttributes(Dictionary<string, string>? attributes) { _attributes = attributes; return this; }

    public UpdateAssetRequest Build() => new(
        _name, _category, _purchaseDate, _purchasePrice, _manufacturer, _model, _serialNumber, _notes, _valuationMethod, _annualRate,
        _salvageValue, _usefulLifeYears, _linkedAccountId, _attributes);

    public static implicit operator UpdateAssetRequest(UpdateAssetRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SetPaymentRequest"/>: marks the bill paid with no amount in slot 0.</summary>
public sealed class SetPaymentRequestBuilder
{
    private bool _paid = true;
    private decimal? _amount;
    private int _slot;

    public static SetPaymentRequestBuilder A() => new();

    public SetPaymentRequestBuilder Paid(bool paid = true) { _paid = paid; return this; }
    public SetPaymentRequestBuilder ForAmount(decimal? amount) { _amount = amount; return this; }
    public SetPaymentRequestBuilder InSlot(int slot) { _slot = slot; return this; }

    public SetPaymentRequest Build() => new(_paid, _amount, _slot);

    public static implicit operator SetPaymentRequest(SetPaymentRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SetBudgetRequest"/>: a 100.00 monthly limit on "Groceries".</summary>
public sealed class SetBudgetRequestBuilder
{
    private string _category = "Groceries";
    private decimal _monthlyLimit = 100m;

    public static SetBudgetRequestBuilder A() => new();

    public SetBudgetRequestBuilder ForCategory(string category) { _category = category; return this; }
    public SetBudgetRequestBuilder Limit(decimal monthlyLimit) { _monthlyLimit = monthlyLimit; return this; }

    public SetBudgetRequest Build() => new(_category, _monthlyLimit);

    public static implicit operator SetBudgetRequest(SetBudgetRequestBuilder builder) => builder.Build();
}
