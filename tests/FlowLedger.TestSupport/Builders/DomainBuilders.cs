using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.TestSupport.Builders;

/// <summary>
/// Builds the <see cref="Account"/> aggregate: a Checking account named "Checking" holding 500.00 USD, stamped with
/// <see cref="TestClock.Instant"/> so a test never depends on the wall clock. Name only the fields the test is about.
/// </summary>
public sealed class AccountBuilder
{
    private TenantId _tenant = TenantId.From(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private string _name = "Checking";
    private AccountType _type = AccountType.Checking;
    private decimal _balance = 500m;
    private string _currency = "USD";
    private decimal? _creditLimit;
    private string? _institution;
    private TimeProvider _time = new FixedTime(TestClock.Instant);

    public static AccountBuilder A() => new();

    public AccountBuilder ForTenant(TenantId tenant) { _tenant = tenant; return this; }
    public AccountBuilder ForTenant(Guid tenant) { _tenant = TenantId.From(tenant); return this; }
    public AccountBuilder Named(string name) { _name = name; return this; }
    public AccountBuilder OfType(AccountType type) { _type = type; return this; }
    public AccountBuilder Holding(decimal balance) { _balance = balance; return this; }
    public AccountBuilder InCurrency(string currency) { _currency = currency; return this; }
    public AccountBuilder WithCreditLimit(decimal? limit) { _creditLimit = limit; return this; }
    public AccountBuilder At(string? institution) { _institution = institution; return this; }
    public AccountBuilder CreatedAt(TimeProvider time) { _time = time; return this; }

    public Account Build() => Account.Create(
        _tenant, _name, _type, new Money(_balance, _currency),
        _creditLimit is { } limit ? new Money(limit, _currency) : null, _institution, _time);

    public static implicit operator Account(AccountBuilder builder) => builder.Build();
}

/// <summary>
/// Builds an actual <see cref="Transaction"/> (<see cref="Transaction.RecordActual"/>): a posted 10.00 USD debit on
/// <see cref="TestClock.Today"/> described "Test merchant", with a manual source. <see cref="Pending"/> leaves the posted date empty.
/// </summary>
public sealed class TransactionBuilder
{
    private TenantId _tenant = TenantId.From(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private AccountId _account = AccountId.New();
    private decimal _amount = 10m;
    private string _currency = "USD";
    private TransactionDirection _direction = TransactionDirection.Debit;
    private string _description = "Test merchant";
    private DateOnly _effective = TestClock.Today;
    private bool _posted = true;
    private TransactionSource _source = TransactionSource.Manual;
    private TransactionFingerprint? _fingerprint;
    private TimeProvider _time = new FixedTime(TestClock.Instant);

    public static TransactionBuilder A() => new();

    public TransactionBuilder ForTenant(TenantId tenant) { _tenant = tenant; return this; }
    public TransactionBuilder OnAccount(AccountId account) { _account = account; return this; }
    public TransactionBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public TransactionBuilder InCurrency(string currency) { _currency = currency; return this; }
    public TransactionBuilder WithAmount(Money amount) { _amount = amount.Amount; _currency = amount.Currency.Code; return this; }
    public TransactionBuilder Credit() { _direction = TransactionDirection.Credit; return this; }
    public TransactionBuilder Debit() { _direction = TransactionDirection.Debit; return this; }
    public TransactionBuilder WithDirection(TransactionDirection direction) { _direction = direction; return this; }
    public TransactionBuilder Described(string description) { _description = description; return this; }
    public TransactionBuilder On(DateOnly date) { _effective = date; return this; }
    public TransactionBuilder Pending() { _posted = false; return this; }
    public TransactionBuilder From(TransactionSource source) { _source = source; return this; }
    public TransactionBuilder WithFingerprint(TransactionFingerprint? fingerprint) { _fingerprint = fingerprint; return this; }
    public TransactionBuilder CreatedAt(TimeProvider time) { _time = time; return this; }

    public Transaction Build() => Transaction.RecordActual(
        _tenant, _account, new Money(_amount, _currency), _direction, _description, _effective,
        _posted ? _effective : null, _source, _fingerprint, _time);

    public static implicit operator Transaction(TransactionBuilder builder) => builder.Build();
}

/// <summary>
/// Builds a <see cref="RecurringFlow"/>: a fixed monthly 100.00 USD debit named "Rent" starting <see cref="TestClock.Today"/>'s month
/// on the 1st (2026-01-01), no end date.
/// </summary>
public sealed class RecurringFlowBuilder
{
    private TenantId _tenant = TenantId.From(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private AccountId _account = AccountId.New();
    private string _name = "Rent";
    private decimal _amount = 100m;
    private string _currency = "USD";
    private TransactionDirection _direction = TransactionDirection.Debit;
    private RecurrencePattern _pattern = RecurrencePattern.Monthly(1);
    private DateOnly _start = new(2026, 1, 1);
    private DateOnly? _end;
    private AmountModel _amountModel = AmountModel.Fixed;
    private string? _counterparty;
    private TimeProvider _time = new FixedTime(TestClock.Instant);

    public static RecurringFlowBuilder A() => new();

    public RecurringFlowBuilder ForTenant(TenantId tenant) { _tenant = tenant; return this; }
    public RecurringFlowBuilder OnAccount(AccountId account) { _account = account; return this; }
    public RecurringFlowBuilder Named(string name) { _name = name; return this; }
    public RecurringFlowBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public RecurringFlowBuilder InCurrency(string currency) { _currency = currency; return this; }
    public RecurringFlowBuilder WithAmount(Money amount) { _amount = amount.Amount; _currency = amount.Currency.Code; return this; }
    public RecurringFlowBuilder Credit() { _direction = TransactionDirection.Credit; return this; }
    public RecurringFlowBuilder Debit() { _direction = TransactionDirection.Debit; return this; }
    public RecurringFlowBuilder WithDirection(TransactionDirection direction) { _direction = direction; return this; }
    public RecurringFlowBuilder Recurring(RecurrencePattern pattern) { _pattern = pattern; return this; }
    public RecurringFlowBuilder From(DateOnly start) { _start = start; return this; }
    public RecurringFlowBuilder Until(DateOnly? end) { _end = end; return this; }
    public RecurringFlowBuilder WithAmountModel(AmountModel model) { _amountModel = model; return this; }
    public RecurringFlowBuilder WithCounterparty(string? counterparty) { _counterparty = counterparty; return this; }
    public RecurringFlowBuilder CreatedAt(TimeProvider time) { _time = time; return this; }

    public RecurringFlow Build() => RecurringFlow.Create(
        _tenant, _account, _name, new Money(_amount, _currency), _direction, _pattern, _start, _end,
        _amountModel, null, _counterparty, _time);

    public static implicit operator RecurringFlow(RecurringFlowBuilder builder) => builder.Build();
}

/// <summary>A clock frozen at one instant, used by the builders so aggregates are stamped deterministically.</summary>
internal sealed class FixedTime(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}
