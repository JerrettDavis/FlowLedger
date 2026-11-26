using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="ForecastFlowInput"/>: a monthly 100.00 USD debit on the 1st unless told otherwise.</summary>
public sealed class ForecastFlowBuilder
{
    private AccountId _account = AccountId.New();
    private decimal _amount = 100m;
    private string _currency = "USD";
    private TransactionDirection _direction = TransactionDirection.Debit;
    private RecurrencePattern _pattern = RecurrencePattern.Monthly(1);
    private DateOnly _start = new(2026, 1, 1);
    private DateOnly? _end;
    private string _name = "Flow";

    public static ForecastFlowBuilder A() => new();

    public ForecastFlowBuilder OnAccount(AccountId account) { _account = account; return this; }
    public ForecastFlowBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public ForecastFlowBuilder InCurrency(string currency) { _currency = currency; return this; }
    public ForecastFlowBuilder Credit() { _direction = TransactionDirection.Credit; return this; }
    public ForecastFlowBuilder Debit() { _direction = TransactionDirection.Debit; return this; }
    public ForecastFlowBuilder Recurring(RecurrencePattern pattern) { _pattern = pattern; return this; }
    public ForecastFlowBuilder From(DateOnly start) { _start = start; return this; }
    public ForecastFlowBuilder Until(DateOnly? endExclusive) { _end = endExclusive; return this; }
    public ForecastFlowBuilder Named(string name) { _name = name; return this; }

    public ForecastFlowInput Build() => new()
    {
        FlowId = RecurringFlowId.New(),
        AccountId = _account,
        Amount = new Money(_amount, _currency),
        Direction = _direction,
        Pattern = _pattern,
        FlowStart = _start,
        FlowEnd = _end,
        Name = _name,
    };

    public static implicit operator ForecastFlowInput(ForecastFlowBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ForecastTransactionInput"/>: a posted 10.00 USD debit.</summary>
public sealed class ForecastTransactionBuilder
{
    private TransactionId _id = TransactionId.New();
    private AccountId _account = AccountId.New();
    private decimal _amount = 10m;
    private string _currency = "USD";
    private TransactionDirection _direction = TransactionDirection.Debit;
    private DateOnly _date = new(2026, 1, 1);
    private TransactionStatus _status = TransactionStatus.Posted;
    private PlannedOccurrenceId? _matched;
    private string? _description;

    public static ForecastTransactionBuilder A() => new();

    public ForecastTransactionBuilder WithId(TransactionId id) { _id = id; return this; }
    public ForecastTransactionBuilder OnAccount(AccountId account) { _account = account; return this; }
    public ForecastTransactionBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public ForecastTransactionBuilder InCurrency(string currency) { _currency = currency; return this; }
    public ForecastTransactionBuilder Credit() { _direction = TransactionDirection.Credit; return this; }
    public ForecastTransactionBuilder Debit() { _direction = TransactionDirection.Debit; return this; }
    public ForecastTransactionBuilder On(DateOnly date) { _date = date; return this; }
    public ForecastTransactionBuilder WithStatus(TransactionStatus status) { _status = status; return this; }
    public ForecastTransactionBuilder MatchedTo(PlannedOccurrenceId occurrence) { _matched = occurrence; return this; }
    public ForecastTransactionBuilder Described(string description) { _description = description; return this; }

    public ForecastTransactionInput Build() => new()
    {
        TransactionId = _id,
        AccountId = _account,
        Amount = new Money(_amount, _currency),
        Direction = _direction,
        EffectiveDate = _date,
        Status = _status,
        MatchedOccurrenceId = _matched,
        Description = _description,
    };

    public static implicit operator ForecastTransactionInput(ForecastTransactionBuilder builder) => builder.Build();
}
