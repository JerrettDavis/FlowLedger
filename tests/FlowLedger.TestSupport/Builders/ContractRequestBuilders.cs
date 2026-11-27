namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="CreateCategoryRequest"/>: a top-level category whose display name is its path.</summary>
public sealed class CreateCategoryRequestBuilder
{
    private string _path = "category";
    private string? _displayName;
    private Guid? _parentId;

    public static CreateCategoryRequestBuilder A() => new();

    public CreateCategoryRequestBuilder AtPath(string path) { _path = path; return this; }
    public CreateCategoryRequestBuilder Named(string displayName) { _displayName = displayName; return this; }
    public CreateCategoryRequestBuilder Under(Guid? parentId) { _parentId = parentId; return this; }

    public CreateCategoryRequest Build() => new(_path, _displayName ?? _path, _parentId);

    public static implicit operator CreateCategoryRequest(CreateCategoryRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AccountSettingsRequest"/>: active, Standard mode, transactions shown.</summary>
public sealed class AccountSettingsRequestBuilder
{
    private bool _isActive = true;
    private string _mode = "Standard";
    private bool _hideTransactions;

    public static AccountSettingsRequestBuilder A() => new();

    public AccountSettingsRequestBuilder Active(bool isActive) { _isActive = isActive; return this; }
    public AccountSettingsRequestBuilder InMode(string mode) { _mode = mode; return this; }
    public AccountSettingsRequestBuilder HidingTransactions(bool hide = true) { _hideTransactions = hide; return this; }

    public AccountSettingsRequest Build() => new(_isActive, _mode, _hideTransactions);

    public static implicit operator AccountSettingsRequest(AccountSettingsRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RecordValuationRequest"/>: 1.00, no date (the server's today), no source, no note.</summary>
public sealed class RecordValuationRequestBuilder
{
    private decimal _amount = 1m;
    private DateOnly? _asOf;
    private string? _source;
    private string? _note;

    public static RecordValuationRequestBuilder A() => new();

    public RecordValuationRequestBuilder Worth(decimal amount) { _amount = amount; return this; }
    public RecordValuationRequestBuilder AsOf(DateOnly? date) { _asOf = date; return this; }
    public RecordValuationRequestBuilder From(string? source) { _source = source; return this; }
    public RecordValuationRequestBuilder WithNote(string? note) { _note = note; return this; }

    public RecordValuationRequest Build() => new(_amount, _asOf, _source, _note);

    public static implicit operator RecordValuationRequest(RecordValuationRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ImportSheetRequest"/>: an additive (non-replacing) import of the given CSV.</summary>
public sealed class ImportSheetRequestBuilder
{
    private string _csv = string.Empty;
    private bool _replace;

    public static ImportSheetRequestBuilder A() => new();

    public ImportSheetRequestBuilder OfCsv(string csv) { _csv = csv; return this; }
    public ImportSheetRequestBuilder Replacing(bool replace = true) { _replace = replace; return this; }

    public ImportSheetRequest Build() => new(_csv, _replace);

    public static implicit operator ImportSheetRequest(ImportSheetRequestBuilder builder) => builder.Build();
}
