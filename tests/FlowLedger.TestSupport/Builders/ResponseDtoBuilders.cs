namespace FlowLedger.TestSupport.Builders;

// Builders for the wide response DTOs that the wire-format round-trip tests need. Each default populates every optional member, so a
// round trip exercises the whole shape. When a DTO gains a field, only its Build() changes, not every sample.

/// <summary>Builds <see cref="AssetDto"/>: a financed car with one valuation, a valuation spread, attributes and a linked loan.</summary>
public sealed class AssetDtoBuilder
{
    private static readonly DateOnly D = new(2026, 3, 4);
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private Guid _id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private string _name = "Car";
    private string _category = "Vehicle";
    private decimal _currentValue = 18000m;
    private IReadOnlyDictionary<string, string> _attributes = new Dictionary<string, string> { ["vin"] = "x" };
    private IReadOnlyList<AssetValuationDto>? _valuations = [new AssetValuationDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), 100m, D, "Manual", null, At)];

    public static AssetDtoBuilder A() => new();

    public AssetDtoBuilder WithId(Guid id) { _id = id; return this; }
    public AssetDtoBuilder Named(string name) { _name = name; return this; }
    public AssetDtoBuilder InCategory(string category) { _category = category; return this; }
    public AssetDtoBuilder Worth(decimal currentValue) { _currentValue = currentValue; return this; }
    public AssetDtoBuilder WithAttributes(IReadOnlyDictionary<string, string> attributes) { _attributes = attributes; return this; }
    public AssetDtoBuilder WithValuations(IReadOnlyList<AssetValuationDto>? valuations) { _valuations = valuations; return this; }

    public AssetDto Build() => new(_id, _name, _category, "Honda", null, null, null, D, 20000m, "USD", _currentValue, D, 17500m,
        "Depreciation", 0.15m, 2000m, 10, Guid.Parse("22222222-2222-2222-2222-222222222222"), "Loan", 5000m, 13000m, -2000m, _attributes,
        true, At, _valuations, new ValuationSpreadDto(1m, 3m, 2m, 2m, 3, 30));

    public static implicit operator AssetDto(AssetDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SheetRowDto"/>: a twice-monthly bill with a paid March cell and one slot.</summary>
public sealed class SheetRowDtoBuilder
{
    private static readonly DateOnly D = new(2026, 3, 4);

    private Guid _id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private string _name = "Power";

    public static SheetRowDtoBuilder A() => new();

    public SheetRowDtoBuilder WithId(Guid id) { _id = id; return this; }
    public SheetRowDtoBuilder Named(string name) { _name = name; return this; }

    public SheetRowDto Build() => new(_id, _name, "Me", false, true, false, false, true, 5, 90m, null, "Utility", "****1234", "power", false,
        new Dictionary<string, SheetCellDto> { ["2026-03"] = new(true, 88m, D, "Bank", 1, 1) }, BillFrequency.TwiceMonthly, null, D, 20,
        new Dictionary<string, IReadOnlyList<SheetSlotDto>> { ["2026-03"] = [new SheetSlotDto(0, D, true, 88m, D, "Bank")] }, 180m);

    public static implicit operator SheetRowDto(SheetRowDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RecurringSuggestionDto"/>: a monthly 15.49 Netflix charge seen three times.</summary>
public sealed class RecurringSuggestionDtoBuilder
{
    private static readonly DateOnly D = new(2026, 3, 4);

    private string _name = "Netflix";
    private decimal _amount = 15.49m;

    public static RecurringSuggestionDtoBuilder A() => new();

    public RecurringSuggestionDtoBuilder Named(string name) { _name = name; return this; }
    public RecurringSuggestionDtoBuilder WithAmount(decimal amount) { _amount = amount; return this; }

    public RecurringSuggestionDto Build() => new("k", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Checking", _name, "Outflow", _amount,
        "USD", "Fixed", "Monthly", 3, null, null, null, D, D.AddMonths(1), 6, 0.92, true);

    public static implicit operator RecurringSuggestionDto(RecurringSuggestionDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SavedReportDto"/>: a user-made report with the given panels (one by default).</summary>
public sealed class SavedReportDtoBuilder
{
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private string _id = "r1";
    private string _name = "Monthly";
    private List<ReportPanel> _panels = [new ReportPanel("Trend", "transactions | take 5", new VizSpec("line", "month", ["income"], "kind", true))];

    public static SavedReportDtoBuilder A() => new();

    public SavedReportDtoBuilder WithId(string id) { _id = id; return this; }
    public SavedReportDtoBuilder Named(string name) { _name = name; return this; }
    public SavedReportDtoBuilder WithPanels(params ReportPanel[] panels) { _panels = [.. panels]; return this; }

    public SavedReportDto Build() => new(_id, _name, "desc", "Report", false, _panels, At, null);

    public static implicit operator SavedReportDto(SavedReportDtoBuilder builder) => builder.Build();
}
