using FlowLedger.TestSupport.Builders;
using System.Reflection;
using System.Text.Json;

namespace FlowLedger.Web.Tests.ApiClient;

/// <summary>
/// #96: wire-format round trips for the contract groups not covered by <see cref="SharedContractSerializationTests"/>:
/// query/reports, recurring flows, assets and net worth, the bill sheet, and the duplicate/import/sync/local-AI types.
/// Each value is written with the API's options, read with the Web client's options, and must serialize back to identical JSON
/// (and the reverse direction, for request types).
/// </summary>
public sealed class ContractGroupRoundTripTests
{
    private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions WebOptions = (JsonSerializerOptions)typeof(FlowLedger.Web.ApiClient.FlowLedgerApiClient)
        .GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly D = new(2026, 3, 4);
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static void AssertRoundTrips<T>(T value)
    {
        var apiJson = JsonSerializer.Serialize(value, ApiOptions);
        var viaWeb = JsonSerializer.Deserialize<T>(apiJson, WebOptions);
        Assert.NotNull(viaWeb);
        Assert.Equal(apiJson, JsonSerializer.Serialize(viaWeb, ApiOptions));

        var webJson = JsonSerializer.Serialize(value, WebOptions);
        var viaApi = JsonSerializer.Deserialize<T>(webJson, ApiOptions);
        Assert.NotNull(viaApi);
        Assert.Equal(apiJson, JsonSerializer.Serialize(viaApi, ApiOptions));
    }

    [Fact]
    public void Query_result_cells_arrive_in_Web_as_JsonElement_and_keep_their_values()
    {
        var result = new QueryResult(
            [new QueryColumn("d", "date"), new QueryColumn("amount", "number"), new QueryColumn("who", "string")],
            [["2026-03-04", 12.5m, "Cafe"], ["2026-03-05", 0m, null]], Truncated: true, ElapsedMs: 7);

        var back = JsonSerializer.Deserialize<QueryResult>(JsonSerializer.Serialize(result, ApiOptions), WebOptions)!;

        Assert.True(back.Truncated);
        Assert.Equal(2, back.Rows.Count);
        var cell = Assert.IsType<JsonElement>(back.Rows[0][1]);
        Assert.Equal(12.5m, cell.GetDecimal());
        Assert.Equal("Cafe", Assert.IsType<JsonElement>(back.Rows[0][2]).GetString());
        Assert.Null(back.Rows[1][2]); // JSON null binds to a null cell; ChartModel.ToElement handles it
        AssertRoundTrips(result);
    }

    [Fact]
    public void Report_types_round_trip()
    {
        var viz = new VizSpec("line", "month", ["income"], "kind", true);
        var panel = new ReportPanel("Trend", "transactions | take 5", viz);
        var result = new QueryResult([new QueryColumn("n", "number")], [[1], [2]], false, 3);

        AssertRoundTrips(viz);
        AssertRoundTrips(new SavedReportDto("r1", "Monthly", "desc", "Report", false, [panel], At, null));
        AssertRoundTrips(SaveReportRequestBuilder.A().Named("Monthly").WithPanels(panel).Build());
        AssertRoundTrips(new RunReportResultDto("r1", "Monthly", null, "Report",
            [new PanelResultDto("Trend", "q", viz, result, null), new PanelResultDto("Bad", "q", viz, null, "boom")]));
        AssertRoundTrips(new QuerySchemaDto(
            [new DatasetInfo("transactions", "all", [new DatasetColumnInfo("date", "date", "when")])], ["sum"], ["count"], ["=="]));
    }

    [Fact]
    public void Recurring_flow_types_round_trip()
    {
        AssertRoundTrips(new RecurringFlowDto(Id1, Id2, "Rent", 1500m, "USD", "Outflow", "Fixed", "Monthly", 1, null, null, null,
            D, null, null, "Landlord", true, At, 1500m));
        AssertRoundTrips(CreateRecurringFlowRequestBuilder.A().OnAccount(Id2).Named("Pay").WithAmount(2000m).WithDirection("Inflow")
            .Every("TwiceMonthly", dayOfMonth: 1, secondDayOfMonth: 15).From(D, D.AddYears(1)).InCategory(Id1).WithCounterparty(null).Build());
        AssertRoundTrips(UpdateRecurringFlowRequestBuilder.A().WithAmount(9.99m).WithModel("Variable").Build());
        AssertRoundTrips(new RecurringSuggestionDto("k", Id1, "Checking", "Netflix", "Outflow", 15.49m, "USD", "Fixed", "Monthly", 3, null,
            null, null, D, D.AddMonths(1), 6, 0.92, true));
    }

    [Fact]
    public void Asset_and_net_worth_types_round_trip()
    {
        var valuation = new AssetValuationDto(Id1, 100m, D, "Manual", null, At);
        var asset = new AssetDto(Id1, "Car", "Vehicle", "Honda", null, null, null, D, 20000m, "USD", 18000m, D, 17500m, "Depreciation",
            0.15m, 2000m, 10, Id2, "Loan", 5000m, 13000m, -2000m, new Dictionary<string, string> { ["vin"] = "x" }, true, At,
            [valuation], new ValuationSpreadDto(1m, 3m, 2m, 2m, 3, 30));
        var worth = new NetWorthDto(10m, 4m, 3m, 9m, 1m, "USD", true, [new CurrencyTotalDto("EUR", 1m, 0m, 0m, 1m, 0m)]);

        AssertRoundTrips(asset);
        AssertRoundTrips(worth);
        AssertRoundTrips(valuation);
        AssertRoundTrips(new AssetCategoryTemplateDto("Vehicle", "Vehicle", "Depreciation", 0.15m, 10, ["vin"]));
        AssertRoundTrips(new AssetSummaryDto(1, 1m, 1m, 0m, 0m, 1m, [new AssetCategoryBreakdownDto("Vehicle", 1, 1m)], [asset], worth));
        AssertRoundTrips(new AssetRefreshResultDto(true, "ok", asset));
        AssertRoundTrips(CreateAssetRequestBuilder.A().Named("Car").InCategory("Vehicle").Worth(1m).ValuedOn(D).PurchasedOn(D, 1m).MadeBy(null, null)
            .Valued(null).WithAttributes(new Dictionary<string, string> { ["a"] = "b" }).Build());
        AssertRoundTrips(UpdateAssetRequestBuilder.A().Named("Car").InCategory("Vehicle").PurchasedOn(D, 1m).MadeBy(null, null).Valued("Manual", 0.1m).Build());
        AssertRoundTrips(RecordValuationRequestBuilder.A().Worth(5m).AsOf(D).From("Manual").WithNote("n").Build());
    }

    [Fact]
    public void Bill_sheet_types_round_trip()
    {
        var totals = new SheetTotalsDto(new SheetTotalsLine(1m, 2m, 3m, 4m, 5m), new SheetTotalsLine(1m, 1m, 1m, 1m, 1m));
        var row = new SheetRowDto(Id1, "Power", "Me", false, true, false, false, true, 5, 90m, null, "Utility", "****1234", "power", false,
            new Dictionary<string, SheetCellDto> { ["2026-03"] = new(true, 88m, D, "Bank", 1, 1) }, BillFrequency.TwiceMonthly, null, D, 20,
            new Dictionary<string, IReadOnlyList<SheetSlotDto>> { ["2026-03"] = [new SheetSlotDto(0, D, true, 88m, D, "Bank")] }, 180m);
        var view = new SheetViewDto(["2026-03"], "2026-03", [new SheetSectionDto("Home", [row], totals)], totals,
            new SheetMonthSummaryDto("2026-03", 1, 2, 88m, 90m));

        AssertRoundTrips(view);
        AssertRoundTrips(SaveBillRequestBuilder.A().InSection("Home").Named("Power").Perpetual().AutoPaying().DueOn(5).Estimating(90m).MatchingText("power")
            .Recurring(BillFrequency.EveryNWeeks, 2, D).Build());
        AssertRoundTrips(SetPaymentRequestBuilder.A().Paid().ForAmount(88m).InSlot(1).Build());
        AssertRoundTrips(ImportSheetRequestBuilder.A().OfCsv("a,b").Replacing().Build());
        AssertRoundTrips(new SheetImportResult(1, 2, 3));
        AssertRoundTrips(new SheetRefreshResult(1, 2, 3));
    }

    [Fact]
    public void Duplicate_import_sync_and_local_ai_types_round_trip()
    {
        var side = new DuplicateSideDto(Id1, Id2, "AMZN", "Amazon", 5m, "Outflow", "Posted", D, null, "Auto", null, true);
        AssertRoundTrips(new CategoryRuleDto("amzn", "Shopping.Online"));
        AssertRoundTrips(new DuplicateSuggestionDto("k", "High", 0.97, 1, "same amount", side, side with { Id = Id2 }));
        AssertRoundTrips(new ImportSummaryDto(Id1, 5, 1, 1, 0, [new RowErrorDto(3, "a,b", "bad date")]));
        AssertRoundTrips(new MerchantGroupDto("Amazon", [new MerchantOccurrence("AMZN", 2)], 2, 0.8, "Amazon"));
        AssertRoundTrips(new SimilarTransactionDto(Id1, D, "Amazon", "AMZN", 5m, "Outflow", null, 0.9));
        AssertRoundTrips(new SyncResult(1, 2, 3, 4));
    }
}
