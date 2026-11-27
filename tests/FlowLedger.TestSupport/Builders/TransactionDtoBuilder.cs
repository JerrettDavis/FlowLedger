namespace FlowLedger.TestSupport.Builders;

/// <summary>
/// Builds <see cref="TransactionDto"/> with sensible defaults so a test only states what it cares about. When
/// <see cref="TransactionDto"/> gains or reorders a field, only <see cref="Build"/> changes, not every test.
/// Defaults: a posted USD debit of 10.00 on a fresh account, dated <see cref="DefaultDate"/>.
/// </summary>
public sealed class TransactionDtoBuilder
{
    /// <summary>Fixed so tests never depend on the wall clock.</summary>
    public static readonly DateOnly DefaultDate = new(2026, 3, 1);

    private Guid _id = Guid.NewGuid();
    private Guid _accountId = Guid.NewGuid();
    private decimal _amount = 10m;
    private string _currency = "USD";
    private string _direction = "Debit";
    private string _description = "Test transaction";
    private string _status = "Posted";
    private string _source = "Import";
    private DateOnly _effectiveDate = DefaultDate;
    private DateOnly? _postedDate;
    private Guid? _categoryId;
    private string? _merchantName;
    private string? _notes;
    private string? _fingerprint;
    private bool _postedDateSet;
    private DateTimeOffset _createdAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private string? _suggestedName;
    private IReadOnlyList<string>? _tags;
    private IReadOnlyList<TransactionSplitDto>? _splits;

    public static TransactionDtoBuilder A() => new();

    /// <summary>Applies a conditional tweak without breaking the fluent chain.</summary>
    public TransactionDtoBuilder With(Func<TransactionDtoBuilder, TransactionDtoBuilder> tweak) => tweak(this);

    public TransactionDtoBuilder WithId(Guid id) { _id = id; return this; }
    public TransactionDtoBuilder OnAccount(Guid accountId) { _accountId = accountId; return this; }
    public TransactionDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public TransactionDtoBuilder InCurrency(string currency) { _currency = currency; return this; }
    public TransactionDtoBuilder WithDirection(string direction) { _direction = direction; return this; }
    public TransactionDtoBuilder WithCreatedAt(DateTimeOffset createdAt) { _createdAt = createdAt; return this; }
    public TransactionDtoBuilder WithSuggestedName(string? suggestedName) { _suggestedName = suggestedName; return this; }
    public TransactionDtoBuilder WithTags(IReadOnlyList<string>? tags) { _tags = tags; return this; }
    public TransactionDtoBuilder WithSplits(IReadOnlyList<TransactionSplitDto>? splits) { _splits = splits; return this; }
    public TransactionDtoBuilder Debit() { _direction = "Debit"; return this; }
    public TransactionDtoBuilder Credit() { _direction = "Credit"; return this; }
    public TransactionDtoBuilder Described(string description) { _description = description; return this; }
    public TransactionDtoBuilder WithStatus(string status) { _status = status; return this; }
    public TransactionDtoBuilder FromSource(string source) { _source = source; return this; }
    public TransactionDtoBuilder WithCategory(Guid? categoryId) { _categoryId = categoryId; return this; }
    public TransactionDtoBuilder WithMerchant(string? merchant) { _merchantName = merchant; return this; }
    public TransactionDtoBuilder WithNotes(string? notes) { _notes = notes; return this; }
    public TransactionDtoBuilder WithFingerprint(string fingerprint) { _fingerprint = fingerprint; return this; }

    /// <summary>Sets both the effective and posted date (the usual case for a settled transaction).</summary>
    public TransactionDtoBuilder On(DateOnly date) { _effectiveDate = date; _postedDate = date; _postedDateSet = true; return this; }

    /// <summary>Sets the effective date to <see cref="DefaultDate"/> plus <paramref name="days"/> (posted date follows).</summary>
    public TransactionDtoBuilder OnDay(int days) => On(DefaultDate.AddDays(days));

    /// <summary>Leaves the posted date null (a pending transaction).</summary>
    public TransactionDtoBuilder Unposted(DateOnly effectiveDate) { _effectiveDate = effectiveDate; _postedDate = null; _postedDateSet = true; return this; }

    public TransactionDto Build() => new(
        _id, _accountId, _amount, _currency, _direction, _description, _status, _source,
        _effectiveDate, _postedDateSet ? _postedDate : _effectiveDate,
        _categoryId, _merchantName, _notes, _fingerprint,
        _createdAt, _suggestedName, _tags, _splits);

    public static implicit operator TransactionDto(TransactionDtoBuilder builder) => builder.Build();
}
