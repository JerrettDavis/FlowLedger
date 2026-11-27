namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="SaveBillRequest"/>: a manual, one-off, non-debt "Bill" in section "Bills" estimated at 100.00, no due day.</summary>
public sealed class SaveBillRequestBuilder
{
    private string _section = "Bills";
    private string _name = "Bill";
    private string? _payer;
    private bool _isDebt;
    private bool _isPerpetual;
    private bool _isOptional;
    private bool _isBusiness;
    private bool _autoPay;
    private int? _dueDay;
    private decimal _estimated = 100m;
    private decimal? _actual;
    private string? _service;
    private string? _accountRef;
    private string? _matchText;
    private string? _frequency;
    private int? _intervalWeeks;
    private DateOnly? _anchorDate;
    private int? _secondDueDay;

    public static SaveBillRequestBuilder A() => new();

    public SaveBillRequestBuilder InSection(string section) { _section = section; return this; }
    public SaveBillRequestBuilder Named(string name) { _name = name; return this; }
    public SaveBillRequestBuilder PaidBy(string? payer) { _payer = payer; return this; }
    public SaveBillRequestBuilder Debt(bool value = true) { _isDebt = value; return this; }
    public SaveBillRequestBuilder Perpetual(bool value = true) { _isPerpetual = value; return this; }
    public SaveBillRequestBuilder Optional(bool value = true) { _isOptional = value; return this; }
    public SaveBillRequestBuilder Business(bool value = true) { _isBusiness = value; return this; }
    public SaveBillRequestBuilder AutoPaying(bool value = true) { _autoPay = value; return this; }
    public SaveBillRequestBuilder DueOn(int? dueDay, int? secondDueDay = null) { _dueDay = dueDay; _secondDueDay = secondDueDay; return this; }
    public SaveBillRequestBuilder Estimating(decimal estimated) { _estimated = estimated; return this; }
    public SaveBillRequestBuilder ActuallyCosting(decimal? actual) { _actual = actual; return this; }
    public SaveBillRequestBuilder ForService(string? service) { _service = service; return this; }
    public SaveBillRequestBuilder WithAccountRef(string? accountRef) { _accountRef = accountRef; return this; }
    public SaveBillRequestBuilder MatchingText(string? matchText) { _matchText = matchText; return this; }
    public SaveBillRequestBuilder Recurring(string? frequency, int? intervalWeeks = null, DateOnly? anchorDate = null)
    {
        _frequency = frequency;
        _intervalWeeks = intervalWeeks;
        _anchorDate = anchorDate;
        return this;
    }

    public SaveBillRequest Build() => new(
        _section, _name, _payer, _isDebt, _isPerpetual, _isOptional, _isBusiness, _autoPay, _dueDay, _estimated, _actual, _service, _accountRef,
        _matchText, _frequency, _intervalWeeks, _anchorDate, _secondDueDay);

    public static implicit operator SaveBillRequest(SaveBillRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SaveReportRequest"/>: a "View" named "Report" with one table panel over <c>transactions | take 1</c>.</summary>
public sealed class SaveReportRequestBuilder
{
    private string _name = "Report";
    private string? _description;
    private string _kind = "View";
    private List<ReportPanel> _panels = [new ReportPanel("p", "transactions | take 1", new VizSpec("table"))];

    public static SaveReportRequestBuilder A() => new();

    public SaveReportRequestBuilder Named(string name) { _name = name; return this; }
    public SaveReportRequestBuilder Described(string? description) { _description = description; return this; }
    public SaveReportRequestBuilder OfKind(string kind) { _kind = kind; return this; }
    public SaveReportRequestBuilder WithPanels(params ReportPanel[] panels) { _panels = [.. panels]; return this; }

    public SaveReportRequest Build() => new(_name, _description, _kind, _panels);

    public static implicit operator SaveReportRequest(SaveReportRequestBuilder builder) => builder.Build();
}
