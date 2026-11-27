using FlowLedger.Application.Features.Query;

namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="ChangeAccountTypeRequest"/>: defaults to a retype to Checking.</summary>
public sealed class ChangeAccountTypeRequestBuilder
{
    private string _accountType = "Checking";

    public static ChangeAccountTypeRequestBuilder A() => new();

    public ChangeAccountTypeRequestBuilder To(string accountType) { _accountType = accountType; return this; }

    public ChangeAccountTypeRequest Build() => new(_accountType);

    public static implicit operator ChangeAccountTypeRequest(ChangeAccountTypeRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RunQueryRequest"/>: defaults to a plain <c>transactions</c> query.</summary>
public sealed class RunQueryRequestBuilder
{
    private string _query = "transactions";

    public static RunQueryRequestBuilder A() => new();

    public RunQueryRequestBuilder Running(string query) { _query = query; return this; }

    public RunQueryRequest Build() => new(_query);

    public static implicit operator RunQueryRequest(RunQueryRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="AskQueryRequest"/>: defaults to a spending question.</summary>
public sealed class AskQueryRequestBuilder
{
    private string _question = "how much did I spend?";

    public static AskQueryRequestBuilder A() => new();

    public AskQueryRequestBuilder Asking(string question) { _question = question; return this; }

    public AskQueryRequest Build() => new(_question);

    public static implicit operator AskQueryRequest(AskQueryRequestBuilder builder) => builder.Build();
}

/// <summary>
/// Builds <see cref="RecurringFlowDto"/>: an active monthly USD outflow of 10.00 on the 1st. When the DTO gains a field
/// only <see cref="Build"/> changes, so the round-trip samples that use it need no edit.
/// </summary>
public sealed class RecurringFlowDtoBuilder
{
    private static readonly DateOnly DefaultStart = new(2026, 3, 1);

    private Guid _id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private Guid _accountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private string _name = "Rent";
    private decimal _amount = 10m;
    private string _direction = "Outflow";
    private string _amountModel = "Fixed";
    private string _frequency = "Monthly";
    private int? _dayOfMonth = 1;
    private string? _counterparty;
    private bool _isActive = true;

    public static RecurringFlowDtoBuilder A() => new();

    public RecurringFlowDtoBuilder WithId(Guid id) { _id = id; return this; }
    public RecurringFlowDtoBuilder OnAccount(Guid accountId) { _accountId = accountId; return this; }
    public RecurringFlowDtoBuilder Named(string name) { _name = name; return this; }
    public RecurringFlowDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }
    public RecurringFlowDtoBuilder WithDirection(string direction) { _direction = direction; return this; }
    public RecurringFlowDtoBuilder WithModel(string amountModel) { _amountModel = amountModel; return this; }
    public RecurringFlowDtoBuilder Every(string frequency, int? dayOfMonth) { _frequency = frequency; _dayOfMonth = dayOfMonth; return this; }
    public RecurringFlowDtoBuilder WithCounterparty(string? counterparty) { _counterparty = counterparty; return this; }
    public RecurringFlowDtoBuilder Active(bool isActive) { _isActive = isActive; return this; }

    public RecurringFlowDto Build() => new(_id, _accountId, _name, _amount, "USD", _direction, _amountModel, _frequency, _dayOfMonth,
        SecondDayOfMonth: null, IntervalWeeks: null, AnchorDayOfWeek: null, StartDate: DefaultStart, EndDate: null, CategoryId: null,
        Counterparty: _counterparty, IsActive: _isActive, CreatedAt: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
        MonthlyAmount: _amount);

    public static implicit operator RecurringFlowDto(RecurringFlowDtoBuilder builder) => builder.Build();
}
