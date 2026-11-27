using FlowLedger.Application.Features.Strategies;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.TestSupport.Builders;

// Builders for the response/contract DTOs the serialization, wire-format and mapping tests construct (#173). Each default is a
// representative value; when a DTO gains a field, only its builder changes, not every test.

/// <summary>Builds <see cref="AccountDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AccountDtoBuilder
{
    private Guid _id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private string _name = "Checking";
    private string _accountType = "Checking";
    private decimal _balanceAmount = 12.34m;
    private string _balanceCurrency = "USD";
    private string? _institution = "Bank";
    private string? _externalAccountRef = "ext-1";
    private bool _isActive = true;
    private DateTimeOffset _createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private DateTimeOffset? _lastBalanceConfirmedAt;
    private bool _typeLockedByUser = true;
    private string _mode = "Holding";
    private bool _hideTransactions = true;
    private uint _version;

    public static AccountDtoBuilder A() => new();

    public AccountDtoBuilder WithId(Guid id) { _id = id; return this; }
    public AccountDtoBuilder WithName(string name) { _name = name; return this; }
    public AccountDtoBuilder WithAccountType(string accountType) { _accountType = accountType; return this; }
    public AccountDtoBuilder WithBalanceAmount(decimal balanceAmount) { _balanceAmount = balanceAmount; return this; }
    public AccountDtoBuilder WithBalanceCurrency(string balanceCurrency) { _balanceCurrency = balanceCurrency; return this; }
    public AccountDtoBuilder WithInstitution(string? institution) { _institution = institution; return this; }
    public AccountDtoBuilder WithExternalAccountRef(string? externalAccountRef) { _externalAccountRef = externalAccountRef; return this; }
    public AccountDtoBuilder WithIsActive(bool isActive) { _isActive = isActive; return this; }
    public AccountDtoBuilder WithCreatedAt(DateTimeOffset createdAt) { _createdAt = createdAt; return this; }
    public AccountDtoBuilder WithLastBalanceConfirmedAt(DateTimeOffset? lastBalanceConfirmedAt) { _lastBalanceConfirmedAt = lastBalanceConfirmedAt; return this; }
    public AccountDtoBuilder WithTypeLockedByUser(bool typeLockedByUser) { _typeLockedByUser = typeLockedByUser; return this; }
    public AccountDtoBuilder WithMode(string mode) { _mode = mode; return this; }
    public AccountDtoBuilder WithHideTransactions(bool hideTransactions) { _hideTransactions = hideTransactions; return this; }
    public AccountDtoBuilder WithVersion(uint version) { _version = version; return this; }

    public AccountDto Build() => new(_id, _name, _accountType, _balanceAmount, _balanceCurrency, _institution, _externalAccountRef, _isActive, _createdAt, _lastBalanceConfirmedAt, _typeLockedByUser, _mode, _hideTransactions, _version);

    public static implicit operator AccountDto(AccountDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AssetCategoryBreakdownDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AssetCategoryBreakdownDtoBuilder
{
    private string _category = "Vehicle";
    private int _count = 1;
    private decimal _value = 1m;

    public static AssetCategoryBreakdownDtoBuilder A() => new();

    public AssetCategoryBreakdownDtoBuilder WithCategory(string category) { _category = category; return this; }
    public AssetCategoryBreakdownDtoBuilder WithCount(int count) { _count = count; return this; }
    public AssetCategoryBreakdownDtoBuilder WithValue(decimal @value) { _value = @value; return this; }

    public AssetCategoryBreakdownDto Build() => new(_category, _count, _value);

    public static implicit operator AssetCategoryBreakdownDto(AssetCategoryBreakdownDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AssetCategoryTemplateDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AssetCategoryTemplateDtoBuilder
{
    private string _category = "Vehicle";
    private string _label = "Vehicle";
    private string _defaultMethod = "Depreciation";
    private decimal _defaultAnnualRate = 0.15m;
    private int? _defaultUsefulLifeYears = 10;
    private IReadOnlyList<string> _suggestedFields = ["vin"];

    public static AssetCategoryTemplateDtoBuilder A() => new();

    public AssetCategoryTemplateDtoBuilder WithCategory(string category) { _category = category; return this; }
    public AssetCategoryTemplateDtoBuilder WithLabel(string label) { _label = label; return this; }
    public AssetCategoryTemplateDtoBuilder WithDefaultMethod(string defaultMethod) { _defaultMethod = defaultMethod; return this; }
    public AssetCategoryTemplateDtoBuilder WithDefaultAnnualRate(decimal defaultAnnualRate) { _defaultAnnualRate = defaultAnnualRate; return this; }
    public AssetCategoryTemplateDtoBuilder WithDefaultUsefulLifeYears(int? defaultUsefulLifeYears) { _defaultUsefulLifeYears = defaultUsefulLifeYears; return this; }
    public AssetCategoryTemplateDtoBuilder WithSuggestedFields(IReadOnlyList<string> suggestedFields) { _suggestedFields = suggestedFields; return this; }

    public AssetCategoryTemplateDto Build() => new(_category, _label, _defaultMethod, _defaultAnnualRate, _defaultUsefulLifeYears, _suggestedFields);

    public static implicit operator AssetCategoryTemplateDto(AssetCategoryTemplateDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AssetRefreshResultDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AssetRefreshResultDtoBuilder
{
    private bool _updated = true;
    private string _message = "ok";
    private AssetDto? _asset;

    public static AssetRefreshResultDtoBuilder A() => new();

    public AssetRefreshResultDtoBuilder WithUpdated(bool updated) { _updated = updated; return this; }
    public AssetRefreshResultDtoBuilder WithMessage(string message) { _message = message; return this; }
    public AssetRefreshResultDtoBuilder WithAsset(AssetDto? asset) { _asset = asset; return this; }

    public AssetRefreshResultDto Build() => new(_updated, _message, _asset);

    public static implicit operator AssetRefreshResultDto(AssetRefreshResultDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AssetSummaryDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AssetSummaryDtoBuilder
{
    private int _assetCount = 1;
    private decimal _totalValue = 1m;
    private decimal _totalCost = 1m;
    private decimal _unrealizedGainLoss;
    private decimal _linkedLiabilities;
    private decimal _totalEquity = 1m;
    private IReadOnlyList<AssetCategoryBreakdownDto> _byCategory = [];
    private IReadOnlyList<AssetDto> _topAssets = [];
    private NetWorthDto _netWorth = NetWorthDtoBuilder.A().Build();

    public static AssetSummaryDtoBuilder A() => new();

    public AssetSummaryDtoBuilder WithAssetCount(int assetCount) { _assetCount = assetCount; return this; }
    public AssetSummaryDtoBuilder WithTotalValue(decimal totalValue) { _totalValue = totalValue; return this; }
    public AssetSummaryDtoBuilder WithTotalCost(decimal totalCost) { _totalCost = totalCost; return this; }
    public AssetSummaryDtoBuilder WithUnrealizedGainLoss(decimal unrealizedGainLoss) { _unrealizedGainLoss = unrealizedGainLoss; return this; }
    public AssetSummaryDtoBuilder WithLinkedLiabilities(decimal linkedLiabilities) { _linkedLiabilities = linkedLiabilities; return this; }
    public AssetSummaryDtoBuilder WithTotalEquity(decimal totalEquity) { _totalEquity = totalEquity; return this; }
    public AssetSummaryDtoBuilder WithByCategory(IReadOnlyList<AssetCategoryBreakdownDto> byCategory) { _byCategory = byCategory; return this; }
    public AssetSummaryDtoBuilder WithTopAssets(IReadOnlyList<AssetDto> topAssets) { _topAssets = topAssets; return this; }
    public AssetSummaryDtoBuilder WithNetWorth(NetWorthDto netWorth) { _netWorth = netWorth; return this; }

    public AssetSummaryDto Build() => new(_assetCount, _totalValue, _totalCost, _unrealizedGainLoss, _linkedLiabilities, _totalEquity, _byCategory, _topAssets, _netWorth);

    public static implicit operator AssetSummaryDto(AssetSummaryDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AssetValuationDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class AssetValuationDtoBuilder
{
    private Guid _id = Guid.Empty;
    private decimal _amount = 100m;
    private DateOnly _asOf;
    private string _source = "Manual";
    private string? _note;
    private DateTimeOffset _recordedAt;

    public static AssetValuationDtoBuilder A() => new();

    public AssetValuationDtoBuilder WithId(Guid id) { _id = id; return this; }
    public AssetValuationDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public AssetValuationDtoBuilder WithAsOf(DateOnly asOf) { _asOf = asOf; return this; }
    public AssetValuationDtoBuilder WithSource(string source) { _source = source; return this; }
    public AssetValuationDtoBuilder WithNote(string? note) { _note = note; return this; }
    public AssetValuationDtoBuilder WithRecordedAt(DateTimeOffset recordedAt) { _recordedAt = recordedAt; return this; }

    public AssetValuationDto Build() => new(_id, _amount, _asOf, _source, _note, _recordedAt);

    public static implicit operator AssetValuationDto(AssetValuationDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CategoryDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class CategoryDtoBuilder
{
    private Guid _id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private string _path = "Food.Dining";
    private string _displayName = "Dining";
    private bool _isSystem;
    private Guid? _parentId;
    private uint _version;

    public static CategoryDtoBuilder A() => new();

    public CategoryDtoBuilder WithId(Guid id) { _id = id; return this; }
    public CategoryDtoBuilder WithPath(string path) { _path = path; return this; }
    public CategoryDtoBuilder WithDisplayName(string displayName) { _displayName = displayName; return this; }
    public CategoryDtoBuilder WithIsSystem(bool isSystem) { _isSystem = isSystem; return this; }
    public CategoryDtoBuilder WithParentId(Guid? parentId) { _parentId = parentId; return this; }
    public CategoryDtoBuilder WithVersion(uint version) { _version = version; return this; }

    public CategoryDto Build() => new(_id, _path, _displayName, _isSystem, _parentId, _version);

    public static implicit operator CategoryDto(CategoryDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CategoryRuleDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class CategoryRuleDtoBuilder
{
    private string _pattern = "SHELL";
    private string _categoryPath = "Auto/Gas";

    public static CategoryRuleDtoBuilder A() => new();

    public CategoryRuleDtoBuilder WithPattern(string pattern) { _pattern = pattern; return this; }
    public CategoryRuleDtoBuilder WithCategoryPath(string categoryPath) { _categoryPath = categoryPath; return this; }

    public CategoryRuleDto Build() => new(_pattern, _categoryPath);

    public static implicit operator CategoryRuleDto(CategoryRuleDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CurrencyTotalDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class CurrencyTotalDtoBuilder
{
    private string _currency = "EUR";
    private decimal _accountAssets = 1m;
    private decimal _accountLiabilities;
    private decimal _physicalAssets;
    private decimal _netWorth = 1m;
    private decimal _carriedByAccounts;

    public static CurrencyTotalDtoBuilder A() => new();

    public CurrencyTotalDtoBuilder WithCurrency(string currency) { _currency = currency; return this; }
    public CurrencyTotalDtoBuilder WithAccountAssets(decimal accountAssets) { _accountAssets = accountAssets; return this; }
    public CurrencyTotalDtoBuilder WithAccountLiabilities(decimal accountLiabilities) { _accountLiabilities = accountLiabilities; return this; }
    public CurrencyTotalDtoBuilder WithPhysicalAssets(decimal physicalAssets) { _physicalAssets = physicalAssets; return this; }
    public CurrencyTotalDtoBuilder WithNetWorth(decimal netWorth) { _netWorth = netWorth; return this; }
    public CurrencyTotalDtoBuilder WithCarriedByAccounts(decimal carriedByAccounts) { _carriedByAccounts = carriedByAccounts; return this; }

    public CurrencyTotalDto Build() => new(_currency, _accountAssets, _accountLiabilities, _physicalAssets, _netWorth, _carriedByAccounts);

    public static implicit operator CurrencyTotalDto(CurrencyTotalDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="DashboardActivityDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class DashboardActivityDtoBuilder
{
    private IReadOnlyList<TransactionDto> _recentPosted = [];
    private IReadOnlyList<TransactionDto> _pending = [];
    private decimal _pendingNetAmount;
    private IReadOnlyList<TransactionDto> _large = [];
    private decimal _largeThreshold = 500m;
    private IReadOnlyList<FlaggedTransactionDto> _suspicious = [];
    private decimal _moneyInLast30Days = 1m;
    private decimal _moneyOutLast30Days = 2m;
    private int _transfersExcluded;

    public static DashboardActivityDtoBuilder A() => new();

    public DashboardActivityDtoBuilder WithRecentPosted(IReadOnlyList<TransactionDto> recentPosted) { _recentPosted = recentPosted; return this; }
    public DashboardActivityDtoBuilder WithPending(IReadOnlyList<TransactionDto> pending) { _pending = pending; return this; }
    public DashboardActivityDtoBuilder WithPendingNetAmount(decimal pendingNetAmount) { _pendingNetAmount = pendingNetAmount; return this; }
    public DashboardActivityDtoBuilder WithLarge(IReadOnlyList<TransactionDto> large) { _large = large; return this; }
    public DashboardActivityDtoBuilder WithLargeThreshold(decimal largeThreshold) { _largeThreshold = largeThreshold; return this; }
    public DashboardActivityDtoBuilder WithSuspicious(IReadOnlyList<FlaggedTransactionDto> suspicious) { _suspicious = suspicious; return this; }
    public DashboardActivityDtoBuilder WithMoneyInLast30Days(decimal moneyInLast30Days) { _moneyInLast30Days = moneyInLast30Days; return this; }
    public DashboardActivityDtoBuilder WithMoneyOutLast30Days(decimal moneyOutLast30Days) { _moneyOutLast30Days = moneyOutLast30Days; return this; }
    public DashboardActivityDtoBuilder WithTransfersExcluded(int transfersExcluded) { _transfersExcluded = transfersExcluded; return this; }

    public DashboardActivityDto Build() => new(_recentPosted, _pending, _pendingNetAmount, _large, _largeThreshold, _suspicious, _moneyInLast30Days, _moneyOutLast30Days, _transfersExcluded);

    public static implicit operator DashboardActivityDto(DashboardActivityDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="DatasetColumnInfo"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class DatasetColumnInfoBuilder
{
    private string _name = "date";
    private string _type = "date";
    private string _description = "when";

    public static DatasetColumnInfoBuilder A() => new();

    public DatasetColumnInfoBuilder WithName(string name) { _name = name; return this; }
    public DatasetColumnInfoBuilder WithType(string type) { _type = type; return this; }
    public DatasetColumnInfoBuilder WithDescription(string description) { _description = description; return this; }

    public DatasetColumnInfo Build() => new(_name, _type, _description);

    public static implicit operator DatasetColumnInfo(DatasetColumnInfoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="DatasetInfo"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class DatasetInfoBuilder
{
    private string _name = "transactions";
    private string _description = "all";
    private IReadOnlyList<DatasetColumnInfo> _columns = [];

    public static DatasetInfoBuilder A() => new();

    public DatasetInfoBuilder WithName(string name) { _name = name; return this; }
    public DatasetInfoBuilder WithDescription(string description) { _description = description; return this; }
    public DatasetInfoBuilder WithColumns(IReadOnlyList<DatasetColumnInfo> columns) { _columns = columns; return this; }

    public DatasetInfo Build() => new(_name, _description, _columns);

    public static implicit operator DatasetInfo(DatasetInfoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="DuplicateSideDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class DuplicateSideDtoBuilder
{
    private Guid _id = Guid.Empty;
    private Guid _accountId = Guid.Empty;
    private string _description = "AMZN";
    private string? _merchantName = "Amazon";
    private decimal _amount = 5m;
    private string _direction = "Outflow";
    private string _status = "Posted";
    private DateOnly _effectiveDate;
    private Guid? _categoryId;
    private string _categorySource = "Auto";
    private string? _notes;
    private bool _bankSynced = true;

    public static DuplicateSideDtoBuilder A() => new();

    public DuplicateSideDtoBuilder WithId(Guid id) { _id = id; return this; }
    public DuplicateSideDtoBuilder WithAccountId(Guid accountId) { _accountId = accountId; return this; }
    public DuplicateSideDtoBuilder WithDescription(string description) { _description = description; return this; }
    public DuplicateSideDtoBuilder WithMerchantName(string? merchantName) { _merchantName = merchantName; return this; }
    public DuplicateSideDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public DuplicateSideDtoBuilder WithDirection(string direction) { _direction = direction; return this; }
    public DuplicateSideDtoBuilder WithStatus(string status) { _status = status; return this; }
    public DuplicateSideDtoBuilder WithEffectiveDate(DateOnly effectiveDate) { _effectiveDate = effectiveDate; return this; }
    public DuplicateSideDtoBuilder WithCategoryId(Guid? categoryId) { _categoryId = categoryId; return this; }
    public DuplicateSideDtoBuilder WithCategorySource(string categorySource) { _categorySource = categorySource; return this; }
    public DuplicateSideDtoBuilder WithNotes(string? notes) { _notes = notes; return this; }
    public DuplicateSideDtoBuilder WithBankSynced(bool bankSynced) { _bankSynced = bankSynced; return this; }

    public DuplicateSideDto Build() => new(_id, _accountId, _description, _merchantName, _amount, _direction, _status, _effectiveDate, _categoryId, _categorySource, _notes, _bankSynced);

    public static implicit operator DuplicateSideDto(DuplicateSideDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="DuplicateSuggestionDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class DuplicateSuggestionDtoBuilder
{
    private string _key = "k";
    private string _confidence = "High";
    private double _score = 0.97;
    private int _daysApart = 1;
    private string _reason = "same amount";
    private DuplicateSideDto _keep = DuplicateSideDtoBuilder.A().Build();
    private DuplicateSideDto _remove = DuplicateSideDtoBuilder.A().Build();

    public static DuplicateSuggestionDtoBuilder A() => new();

    public DuplicateSuggestionDtoBuilder WithKey(string key) { _key = key; return this; }
    public DuplicateSuggestionDtoBuilder WithConfidence(string confidence) { _confidence = confidence; return this; }
    public DuplicateSuggestionDtoBuilder WithScore(double score) { _score = score; return this; }
    public DuplicateSuggestionDtoBuilder WithDaysApart(int daysApart) { _daysApart = daysApart; return this; }
    public DuplicateSuggestionDtoBuilder WithReason(string reason) { _reason = reason; return this; }
    public DuplicateSuggestionDtoBuilder WithKeep(DuplicateSideDto keep) { _keep = keep; return this; }
    public DuplicateSuggestionDtoBuilder WithRemove(DuplicateSideDto remove) { _remove = remove; return this; }

    public DuplicateSuggestionDto Build() => new(_key, _confidence, _score, _daysApart, _reason, _keep, _remove);

    public static implicit operator DuplicateSuggestionDto(DuplicateSuggestionDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="FlaggedTransactionDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class FlaggedTransactionDtoBuilder
{
    private TransactionDto _transaction = TransactionDtoBuilder.A().Build();
    private IReadOnlyList<string> _reasons = ["big"];

    public static FlaggedTransactionDtoBuilder A() => new();

    public FlaggedTransactionDtoBuilder WithTransaction(TransactionDto transaction) { _transaction = transaction; return this; }
    public FlaggedTransactionDtoBuilder WithReasons(IReadOnlyList<string> reasons) { _reasons = reasons; return this; }

    public FlaggedTransactionDto Build() => new(_transaction, _reasons);

    public static implicit operator FlaggedTransactionDto(FlaggedTransactionDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ImportSummaryDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class ImportSummaryDtoBuilder
{
    private Guid _importBatchId = Guid.Empty;
    private int _importedCount = 5;
    private int _duplicateCount = 1;
    private int _failedRowCount = 1;
    private int _matchedToPlanCount;
    private IReadOnlyList<RowErrorDto> _rowErrors = [];

    public static ImportSummaryDtoBuilder A() => new();

    public ImportSummaryDtoBuilder WithImportBatchId(Guid importBatchId) { _importBatchId = importBatchId; return this; }
    public ImportSummaryDtoBuilder WithImportedCount(int importedCount) { _importedCount = importedCount; return this; }
    public ImportSummaryDtoBuilder WithDuplicateCount(int duplicateCount) { _duplicateCount = duplicateCount; return this; }
    public ImportSummaryDtoBuilder WithFailedRowCount(int failedRowCount) { _failedRowCount = failedRowCount; return this; }
    public ImportSummaryDtoBuilder WithMatchedToPlanCount(int matchedToPlanCount) { _matchedToPlanCount = matchedToPlanCount; return this; }
    public ImportSummaryDtoBuilder WithRowErrors(IReadOnlyList<RowErrorDto> rowErrors) { _rowErrors = rowErrors; return this; }

    public ImportSummaryDto Build() => new(_importBatchId, _importedCount, _duplicateCount, _failedRowCount, _matchedToPlanCount, _rowErrors);

    public static implicit operator ImportSummaryDto(ImportSummaryDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="MerchantAliasDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class MerchantAliasDtoBuilder
{
    private string _pattern = "";
    private string _alias = "";

    public static MerchantAliasDtoBuilder A() => new();

    public MerchantAliasDtoBuilder WithPattern(string pattern) { _pattern = pattern; return this; }
    public MerchantAliasDtoBuilder WithAlias(string alias) { _alias = alias; return this; }

    public MerchantAliasDto Build() => new(_pattern, _alias);

    public static implicit operator MerchantAliasDto(MerchantAliasDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="MerchantGroupDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class MerchantGroupDtoBuilder
{
    private string _canonical = "Amazon";
    private IReadOnlyList<MerchantOccurrence> _members = [];
    private int _totalCount = 2;
    private double _minSimilarity = 0.8;
    private string _suggestedName = "Amazon";

    public static MerchantGroupDtoBuilder A() => new();

    public MerchantGroupDtoBuilder WithCanonical(string canonical) { _canonical = canonical; return this; }
    public MerchantGroupDtoBuilder WithMembers(IReadOnlyList<MerchantOccurrence> members) { _members = members; return this; }
    public MerchantGroupDtoBuilder WithTotalCount(int totalCount) { _totalCount = totalCount; return this; }
    public MerchantGroupDtoBuilder WithMinSimilarity(double minSimilarity) { _minSimilarity = minSimilarity; return this; }
    public MerchantGroupDtoBuilder WithSuggestedName(string suggestedName) { _suggestedName = suggestedName; return this; }

    public MerchantGroupDto Build() => new(_canonical, _members, _totalCount, _minSimilarity, _suggestedName);

    public static implicit operator MerchantGroupDto(MerchantGroupDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="MerchantOccurrence"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class MerchantOccurrenceBuilder
{
    private string _raw = "STARBUCKS #1042";
    private int _count = 12;

    public static MerchantOccurrenceBuilder A() => new();

    public MerchantOccurrenceBuilder WithRaw(string raw) { _raw = raw; return this; }
    public MerchantOccurrenceBuilder WithCount(int count) { _count = count; return this; }

    public MerchantOccurrence Build() => new(_raw, _count);

    public static implicit operator MerchantOccurrence(MerchantOccurrenceBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="NetWorthDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class NetWorthDtoBuilder
{
    private decimal _accountAssets = 10m;
    private decimal _accountLiabilities = 4m;
    private decimal _physicalAssets = 3m;
    private decimal _netWorth = 9m;
    private decimal _carriedByAccounts = 1m;
    private string _currency = "USD";
    private bool _mixedCurrencies = true;
    private IReadOnlyList<CurrencyTotalDto>? _byCurrency;

    public static NetWorthDtoBuilder A() => new();

    public NetWorthDtoBuilder WithAccountAssets(decimal accountAssets) { _accountAssets = accountAssets; return this; }
    public NetWorthDtoBuilder WithAccountLiabilities(decimal accountLiabilities) { _accountLiabilities = accountLiabilities; return this; }
    public NetWorthDtoBuilder WithPhysicalAssets(decimal physicalAssets) { _physicalAssets = physicalAssets; return this; }
    public NetWorthDtoBuilder WithNetWorth(decimal netWorth) { _netWorth = netWorth; return this; }
    public NetWorthDtoBuilder WithCarriedByAccounts(decimal carriedByAccounts) { _carriedByAccounts = carriedByAccounts; return this; }
    public NetWorthDtoBuilder WithCurrency(string currency) { _currency = currency; return this; }
    public NetWorthDtoBuilder WithMixedCurrencies(bool mixedCurrencies) { _mixedCurrencies = mixedCurrencies; return this; }
    public NetWorthDtoBuilder WithByCurrency(IReadOnlyList<CurrencyTotalDto>? byCurrency) { _byCurrency = byCurrency; return this; }

    public NetWorthDto Build() => new(_accountAssets, _accountLiabilities, _physicalAssets, _netWorth, _carriedByAccounts, _currency, _mixedCurrencies, _byCurrency);

    public static implicit operator NetWorthDto(NetWorthDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="PanelResultDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class PanelResultDtoBuilder
{
    private string _title = "Trend";
    private string _query = "q";
    private VizSpec _viz = VizSpecBuilder.A().Build();
    private QueryResult? _result;
    private string? _error;

    public static PanelResultDtoBuilder A() => new();

    public PanelResultDtoBuilder WithTitle(string title) { _title = title; return this; }
    public PanelResultDtoBuilder WithQuery(string query) { _query = query; return this; }
    public PanelResultDtoBuilder WithViz(VizSpec viz) { _viz = viz; return this; }
    public PanelResultDtoBuilder WithResult(QueryResult? result) { _result = result; return this; }
    public PanelResultDtoBuilder WithError(string? error) { _error = error; return this; }

    public PanelResultDto Build() => new(_title, _query, _viz, _result, _error);

    public static implicit operator PanelResultDto(PanelResultDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="QueryColumn"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class QueryColumnBuilder
{
    private string _name = "n";
    private string _type = "number";

    public static QueryColumnBuilder A() => new();

    public QueryColumnBuilder WithName(string name) { _name = name; return this; }
    public QueryColumnBuilder WithType(string type) { _type = type; return this; }

    public QueryColumn Build() => new(_name, _type);

    public static implicit operator QueryColumn(QueryColumnBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="QueryResult"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class QueryResultBuilder
{
    private IReadOnlyList<QueryColumn> _columns = [];
    private IReadOnlyList<IReadOnlyList<object?>> _rows = [new object?[] { "Food/Groceries", 150m }, new object?[] { null, 5.5d }, new object?[] { "Rent", null }];
    private bool _truncated;
    private long _elapsedMs = 1;

    public static QueryResultBuilder A() => new();

    public QueryResultBuilder WithColumns(IReadOnlyList<QueryColumn> columns) { _columns = columns; return this; }
    public QueryResultBuilder WithRows(IReadOnlyList<IReadOnlyList<object?>> rows) { _rows = rows; return this; }
    public QueryResultBuilder WithTruncated(bool truncated) { _truncated = truncated; return this; }
    public QueryResultBuilder WithElapsedMs(long elapsedMs) { _elapsedMs = elapsedMs; return this; }

    public QueryResult Build() => new(_columns, _rows, _truncated, _elapsedMs);

    public static implicit operator QueryResult(QueryResultBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="QuerySchemaDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class QuerySchemaDtoBuilder
{
    private IReadOnlyList<DatasetInfo> _datasets = [];
    private IReadOnlyList<string> _functions = ["sum"];
    private IReadOnlyList<string> _aggregates = ["count"];
    private IReadOnlyList<string> _operators = ["=="];

    public static QuerySchemaDtoBuilder A() => new();

    public QuerySchemaDtoBuilder WithDatasets(IReadOnlyList<DatasetInfo> datasets) { _datasets = datasets; return this; }
    public QuerySchemaDtoBuilder WithFunctions(IReadOnlyList<string> functions) { _functions = functions; return this; }
    public QuerySchemaDtoBuilder WithAggregates(IReadOnlyList<string> aggregates) { _aggregates = aggregates; return this; }
    public QuerySchemaDtoBuilder WithOperators(IReadOnlyList<string> operators) { _operators = operators; return this; }

    public QuerySchemaDto Build() => new(_datasets, _functions, _aggregates, _operators);

    public static implicit operator QuerySchemaDto(QuerySchemaDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ReportPanel"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class ReportPanelBuilder
{
    private string _title = "p";
    private string _query = "transactions | summarize t = sum(amount) by merchant";
    private VizSpec _viz = VizSpecBuilder.A().Build();

    public static ReportPanelBuilder A() => new();

    public ReportPanelBuilder WithTitle(string title) { _title = title; return this; }
    public ReportPanelBuilder WithQuery(string query) { _query = query; return this; }
    public ReportPanelBuilder WithViz(VizSpec viz) { _viz = viz; return this; }

    public ReportPanel Build() => new(_title, _query, _viz);

    public static implicit operator ReportPanel(ReportPanelBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RowErrorDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class RowErrorDtoBuilder
{
    private int _rowNumber = 3;
    private string _rawLine = "a,b";
    private string _error = "bad date";

    public static RowErrorDtoBuilder A() => new();

    public RowErrorDtoBuilder WithRowNumber(int rowNumber) { _rowNumber = rowNumber; return this; }
    public RowErrorDtoBuilder WithRawLine(string rawLine) { _rawLine = rawLine; return this; }
    public RowErrorDtoBuilder WithError(string error) { _error = error; return this; }

    public RowErrorDto Build() => new(_rowNumber, _rawLine, _error);

    public static implicit operator RowErrorDto(RowErrorDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RunReportResultDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class RunReportResultDtoBuilder
{
    private string _id = "r1";
    private string _name = "Monthly";
    private string? _description;
    private string _kind = "Report";
    private List<PanelResultDto> _panels = [];

    public static RunReportResultDtoBuilder A() => new();

    public RunReportResultDtoBuilder WithId(string id) { _id = id; return this; }
    public RunReportResultDtoBuilder WithName(string name) { _name = name; return this; }
    public RunReportResultDtoBuilder WithDescription(string? description) { _description = description; return this; }
    public RunReportResultDtoBuilder WithKind(string kind) { _kind = kind; return this; }
    public RunReportResultDtoBuilder WithPanels(List<PanelResultDto> panels) { _panels = panels; return this; }

    public RunReportResultDto Build() => new(_id, _name, _description, _kind, _panels);

    public static implicit operator RunReportResultDto(RunReportResultDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetImportResult"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetImportResultBuilder
{
    private int _sections = 2;
    private int _bills = 4;
    private int _paidMarks = 3;

    public static SheetImportResultBuilder A() => new();

    public SheetImportResultBuilder WithSections(int sections) { _sections = sections; return this; }
    public SheetImportResultBuilder WithBills(int bills) { _bills = bills; return this; }
    public SheetImportResultBuilder WithPaidMarks(int paidMarks) { _paidMarks = paidMarks; return this; }

    public SheetImportResult Build() => new(_sections, _bills, _paidMarks);

    public static implicit operator SheetImportResult(SheetImportResultBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetMonthSummaryDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetMonthSummaryDtoBuilder
{
    private string _month = "2026-03";
    private int _paidCount = 1;
    private int _totalCount = 2;
    private decimal _paidAmount = 88m;
    private decimal _remainingEstimated = 90m;

    public static SheetMonthSummaryDtoBuilder A() => new();

    public SheetMonthSummaryDtoBuilder WithMonth(string month) { _month = month; return this; }
    public SheetMonthSummaryDtoBuilder WithPaidCount(int paidCount) { _paidCount = paidCount; return this; }
    public SheetMonthSummaryDtoBuilder WithTotalCount(int totalCount) { _totalCount = totalCount; return this; }
    public SheetMonthSummaryDtoBuilder WithPaidAmount(decimal paidAmount) { _paidAmount = paidAmount; return this; }
    public SheetMonthSummaryDtoBuilder WithRemainingEstimated(decimal remainingEstimated) { _remainingEstimated = remainingEstimated; return this; }

    public SheetMonthSummaryDto Build() => new(_month, _paidCount, _totalCount, _paidAmount, _remainingEstimated);

    public static implicit operator SheetMonthSummaryDto(SheetMonthSummaryDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetRefreshResult"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetRefreshResultBuilder
{
    private int _marked;
    private int _cleared;
    private int _added;

    public static SheetRefreshResultBuilder A() => new();

    public SheetRefreshResultBuilder WithMarked(int marked) { _marked = marked; return this; }
    public SheetRefreshResultBuilder WithCleared(int cleared) { _cleared = cleared; return this; }
    public SheetRefreshResultBuilder WithAdded(int added) { _added = added; return this; }

    public SheetRefreshResult Build() => new(_marked, _cleared, _added);

    public static implicit operator SheetRefreshResult(SheetRefreshResultBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetSectionDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetSectionDtoBuilder
{
    private string _name = "Home";
    private IReadOnlyList<SheetRowDto> _rows = [];
    private SheetTotalsDto _totals = SheetTotalsDtoBuilder.A().Build();

    public static SheetSectionDtoBuilder A() => new();

    public SheetSectionDtoBuilder WithName(string name) { _name = name; return this; }
    public SheetSectionDtoBuilder WithRows(IReadOnlyList<SheetRowDto> rows) { _rows = rows; return this; }
    public SheetSectionDtoBuilder WithTotals(SheetTotalsDto totals) { _totals = totals; return this; }

    public SheetSectionDto Build() => new(_name, _rows, _totals);

    public static implicit operator SheetSectionDto(SheetSectionDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetTotalsDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetTotalsDtoBuilder
{
    private SheetTotalsLine _estimated = SheetTotalsLineBuilder.A().Build();
    private SheetTotalsLine _actual = SheetTotalsLineBuilder.A().Build();

    public static SheetTotalsDtoBuilder A() => new();

    public SheetTotalsDtoBuilder WithEstimated(SheetTotalsLine estimated) { _estimated = estimated; return this; }
    public SheetTotalsDtoBuilder WithActual(SheetTotalsLine actual) { _actual = actual; return this; }

    public SheetTotalsDto Build() => new(_estimated, _actual);

    public static implicit operator SheetTotalsDto(SheetTotalsDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetTotalsLine"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetTotalsLineBuilder
{
    private decimal _total = 1m;
    private decimal _optional = 30m;
    private decimal _required = 500m;
    private decimal _personal = 520m;
    private decimal _business = 10m;

    public static SheetTotalsLineBuilder A() => new();

    public SheetTotalsLineBuilder WithTotal(decimal total) { _total = total; return this; }
    public SheetTotalsLineBuilder WithOptional(decimal optional) { _optional = optional; return this; }
    public SheetTotalsLineBuilder WithRequired(decimal required) { _required = required; return this; }
    public SheetTotalsLineBuilder WithPersonal(decimal personal) { _personal = personal; return this; }
    public SheetTotalsLineBuilder WithBusiness(decimal business) { _business = business; return this; }

    public SheetTotalsLine Build() => new(_total, _optional, _required, _personal, _business);

    public static implicit operator SheetTotalsLine(SheetTotalsLineBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetViewDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SheetViewDtoBuilder
{
    private IReadOnlyList<string> _months = ["2026-03"];
    private string _selectedMonth = "2026-03";
    private IReadOnlyList<SheetSectionDto> _sections = [];
    private SheetTotalsDto _grandTotals = SheetTotalsDtoBuilder.A().Build();
    private SheetMonthSummaryDto _summary = SheetMonthSummaryDtoBuilder.A().Build();

    public static SheetViewDtoBuilder A() => new();

    public SheetViewDtoBuilder WithMonths(IReadOnlyList<string> months) { _months = months; return this; }
    public SheetViewDtoBuilder WithSelectedMonth(string selectedMonth) { _selectedMonth = selectedMonth; return this; }
    public SheetViewDtoBuilder WithSections(IReadOnlyList<SheetSectionDto> sections) { _sections = sections; return this; }
    public SheetViewDtoBuilder WithGrandTotals(SheetTotalsDto grandTotals) { _grandTotals = grandTotals; return this; }
    public SheetViewDtoBuilder WithSummary(SheetMonthSummaryDto summary) { _summary = summary; return this; }

    public SheetViewDto Build() => new(_months, _selectedMonth, _sections, _grandTotals, _summary);

    public static implicit operator SheetViewDto(SheetViewDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SimilarTransactionDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SimilarTransactionDtoBuilder
{
    private Guid _id = Guid.Empty;
    private DateOnly _date;
    private string? _merchant = "Amazon";
    private string _description = "AMZN";
    private decimal _amount = 5m;
    private string _direction = "Outflow";
    private string? _categoryPath;
    private double _similarity = 0.9;

    public static SimilarTransactionDtoBuilder A() => new();

    public SimilarTransactionDtoBuilder WithId(Guid id) { _id = id; return this; }
    public SimilarTransactionDtoBuilder WithDate(DateOnly date) { _date = date; return this; }
    public SimilarTransactionDtoBuilder WithMerchant(string? merchant) { _merchant = merchant; return this; }
    public SimilarTransactionDtoBuilder WithDescription(string description) { _description = description; return this; }
    public SimilarTransactionDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public SimilarTransactionDtoBuilder WithDirection(string direction) { _direction = direction; return this; }
    public SimilarTransactionDtoBuilder WithCategoryPath(string? categoryPath) { _categoryPath = categoryPath; return this; }
    public SimilarTransactionDtoBuilder WithSimilarity(double similarity) { _similarity = similarity; return this; }

    public SimilarTransactionDto Build() => new(_id, _date, _merchant, _description, _amount, _direction, _categoryPath, _similarity);

    public static implicit operator SimilarTransactionDto(SimilarTransactionDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SyncResult"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class SyncResultBuilder
{
    private int _accountsUpserted;
    private int _transactionsAdded;
    private int _transactionsSkipped;
    private int _recurringFlowsAdded;
    private IReadOnlyList<Guid>? _changedTransactionIds;

    public static SyncResultBuilder A() => new();

    public SyncResultBuilder WithAccountsUpserted(int accountsUpserted) { _accountsUpserted = accountsUpserted; return this; }
    public SyncResultBuilder WithTransactionsAdded(int transactionsAdded) { _transactionsAdded = transactionsAdded; return this; }
    public SyncResultBuilder WithTransactionsSkipped(int transactionsSkipped) { _transactionsSkipped = transactionsSkipped; return this; }
    public SyncResultBuilder WithRecurringFlowsAdded(int recurringFlowsAdded) { _recurringFlowsAdded = recurringFlowsAdded; return this; }
    public SyncResultBuilder WithChangedTransactionIds(IReadOnlyList<Guid>? changedTransactionIds) { _changedTransactionIds = changedTransactionIds; return this; }

    public SyncResult Build() => new(_accountsUpserted, _transactionsAdded, _transactionsSkipped, _recurringFlowsAdded, _changedTransactionIds);

    public static implicit operator SyncResult(SyncResultBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="TransactionSplitDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class TransactionSplitDtoBuilder
{
    private decimal _amount = 70;
    private Guid? _categoryId;
    private string? _notes;

    public static TransactionSplitDtoBuilder A() => new();

    public TransactionSplitDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public TransactionSplitDtoBuilder WithCategoryId(Guid? categoryId) { _categoryId = categoryId; return this; }
    public TransactionSplitDtoBuilder WithNotes(string? notes) { _notes = notes; return this; }

    public TransactionSplitDto Build() => new(_amount, _categoryId, _notes);

    public static implicit operator TransactionSplitDto(TransactionSplitDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="VizSpec"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class VizSpecBuilder
{
    private string _kind = "column";
    private string? _x;
    private List<string>? _y;
    private string? _seriesBy;
    private bool _stacked;

    public static VizSpecBuilder A() => new();

    public VizSpecBuilder WithKind(string kind) { _kind = kind; return this; }
    public VizSpecBuilder WithX(string? x) { _x = x; return this; }
    public VizSpecBuilder WithY(List<string>? y) { _y = y; return this; }
    public VizSpecBuilder WithSeriesBy(string? seriesBy) { _seriesBy = seriesBy; return this; }
    public VizSpecBuilder WithStacked(bool stacked) { _stacked = stacked; return this; }

    public VizSpec Build() => new(_kind, _x, _y, _seriesBy, _stacked);

    public static implicit operator VizSpec(VizSpecBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="GoalRequest"/>: a 12,000 target reached by saving 1,000 a month, no scenario levers.</summary>
public sealed class GoalRequestBuilder
{
    private decimal _target = 12000m;
    private decimal _saved;
    private decimal _monthly = 1000m;
    private decimal _apy;
    private int _deadlineMonths;
    private decimal _extraMonthly;
    private decimal _incomeRaisePct;
    private decimal _spendCutPct;
    private decimal _lumpSum;
    private decimal _scenarioApy;

    public static GoalRequestBuilder A() => new();

    public GoalRequestBuilder WithTarget(decimal target) { _target = target; return this; }
    public GoalRequestBuilder WithSaved(decimal saved) { _saved = saved; return this; }
    public GoalRequestBuilder WithMonthly(decimal monthly) { _monthly = monthly; return this; }
    public GoalRequestBuilder WithApy(decimal apy) { _apy = apy; return this; }
    public GoalRequestBuilder WithDeadlineMonths(int deadlineMonths) { _deadlineMonths = deadlineMonths; return this; }
    public GoalRequestBuilder WithExtraMonthly(decimal extraMonthly) { _extraMonthly = extraMonthly; return this; }
    public GoalRequestBuilder WithIncomeRaisePct(decimal incomeRaisePct) { _incomeRaisePct = incomeRaisePct; return this; }
    public GoalRequestBuilder WithSpendCutPct(decimal spendCutPct) { _spendCutPct = spendCutPct; return this; }
    public GoalRequestBuilder WithLumpSum(decimal lumpSum) { _lumpSum = lumpSum; return this; }
    public GoalRequestBuilder WithScenarioApy(decimal scenarioApy) { _scenarioApy = scenarioApy; return this; }

    public GoalRequest Build() => new(_target, _saved, _monthly, _apy, _deadlineMonths, _extraMonthly, _incomeRaisePct, _spendCutPct, _lumpSum, _scenarioApy);

    public static implicit operator GoalRequest(GoalRequestBuilder builder) => builder.Build();
}
