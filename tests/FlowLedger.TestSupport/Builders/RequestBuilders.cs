using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.TestSupport.Builders;

/// <summary>
/// Builds <see cref="CreateTransactionRequest"/> with valid defaults (a 100.00 USD debit dated <see cref="DefaultDate"/>) so a
/// test names only the field it is about. A new request field changes <see cref="Build"/>, not every call site.
/// </summary>
public sealed class CreateTransactionRequestBuilder
{
    public static readonly DateOnly DefaultDate = new(2026, 1, 1);

    private Guid _accountId = Guid.NewGuid();
    private decimal _amount = 100m;
    private string _currency = "USD";
    private string _direction = "Debit";
    private string _description = "Test merchant";
    private DateOnly _effectiveDate = DefaultDate;
    private DateOnly? _postedDate;
    private Guid? _categoryId;
    private string? _merchantName;
    private string? _notes;

    public static CreateTransactionRequestBuilder A() => new();

    public CreateTransactionRequestBuilder OnAccount(Guid accountId) { _accountId = accountId; return this; }
    public CreateTransactionRequestBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public CreateTransactionRequestBuilder InCurrency(string currency) { _currency = currency; return this; }
    public CreateTransactionRequestBuilder WithDirection(string direction) { _direction = direction; return this; }
    public CreateTransactionRequestBuilder Described(string description) { _description = description; return this; }
    public CreateTransactionRequestBuilder On(DateOnly date) { _effectiveDate = date; return this; }
    public CreateTransactionRequestBuilder PostedOn(DateOnly? date) { _postedDate = date; return this; }
    public CreateTransactionRequestBuilder InCategory(Guid? categoryId) { _categoryId = categoryId; return this; }
    public CreateTransactionRequestBuilder AtMerchant(string? merchant) { _merchantName = merchant; return this; }
    public CreateTransactionRequestBuilder WithNotes(string? notes) { _notes = notes; return this; }

    public CreateTransactionRequest Build()
        => new(_accountId, _amount, _currency, _direction, _description, _effectiveDate, _postedDate, _categoryId, _merchantName, _notes);

    public static implicit operator CreateTransactionRequest(CreateTransactionRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="CreateAccountRequest"/>: a valid Checking account, 500.00 USD, no institution or limit.</summary>
public sealed class CreateAccountRequestBuilder
{
    private string _name = "Checking";
    private string _accountType = "Checking";
    private decimal _startingBalance = 500m;
    private string _currency = "USD";
    private string? _institution;
    private decimal? _creditLimit;

    public static CreateAccountRequestBuilder A() => new();

    public CreateAccountRequestBuilder Named(string name) { _name = name; return this; }
    public CreateAccountRequestBuilder OfType(string accountType) { _accountType = accountType; return this; }
    public CreateAccountRequestBuilder StartingAt(decimal balance) { _startingBalance = balance; return this; }
    public CreateAccountRequestBuilder InCurrency(string currency) { _currency = currency; return this; }
    public CreateAccountRequestBuilder At(string? institution) { _institution = institution; return this; }
    public CreateAccountRequestBuilder WithCreditLimit(decimal? limit) { _creditLimit = limit; return this; }

    public CreateAccountRequest Build() => new(_name, _accountType, _startingBalance, _currency, _institution, _creditLimit);

    public static implicit operator CreateAccountRequest(CreateAccountRequestBuilder builder) => builder.Build();
}
