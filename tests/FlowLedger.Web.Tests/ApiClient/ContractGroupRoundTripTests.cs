using System.Reflection;
using System.Text.Json;
using FlowLedger.TestSupport.Builders;

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
        var result = QueryResultBuilder.A()
                                 .WithColumns([QueryColumnBuilder.A().WithName("d").WithType("date").Build(), QueryColumnBuilder.A().WithName("amount").Build(), QueryColumnBuilder.A()
                                                                                                .WithName("who")
                                                                                                .WithType("string")
                                                                                                .Build()])
                                 .WithRows([["2026-03-04", 12.5m, "Cafe"], ["2026-03-05", 0m, null]])
                                 .WithTruncated(true)
                                 .WithElapsedMs(7)
                                 .Build();

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
        var viz = VizSpecBuilder.A().WithKind("line").WithX("month").WithY(["income"]).WithSeriesBy("kind").WithStacked(true).Build();
        var panel = ReportPanelBuilder.A().WithTitle("Trend").WithQuery("transactions | take 5").WithViz(viz).Build();
        var result = QueryResultBuilder.A().WithColumns([QueryColumnBuilder.A().Build()]).WithRows([[1], [2]]).WithElapsedMs(3).Build();

        AssertRoundTrips(viz);
        AssertRoundTrips(SavedReportDtoBuilder.A().WithPanels(panel).Build());
        AssertRoundTrips(SaveReportRequestBuilder.A().Named("Monthly").WithPanels(panel).Build());
        AssertRoundTrips(RunReportResultDtoBuilder.A()
                                     .WithPanels([PanelResultDtoBuilder.A().WithViz(viz).WithResult(result).Build(), PanelResultDtoBuilder.A()
                                                                                  .WithTitle("Bad")
                                                                                  .WithViz(viz)
                                                                                  .WithError("boom")
                                                                                  .Build()])
                                     .Build());
        AssertRoundTrips(QuerySchemaDtoBuilder.A()
                                     .WithDatasets([DatasetInfoBuilder.A().WithColumns([DatasetColumnInfoBuilder.A().Build()]).Build()])
                                     .Build());
    }

    [Fact]
    public void Recurring_flow_types_round_trip()
    {
        AssertRoundTrips(RecurringFlowDtoBuilder.A().WithId(Id1).OnAccount(Id2).Named("Rent").WithAmount(1500m).WithCounterparty("Landlord").Build());
        AssertRoundTrips(CreateRecurringFlowRequestBuilder.A().OnAccount(Id2).Named("Pay").WithAmount(2000m).WithDirection("Inflow")
            .Every("TwiceMonthly", dayOfMonth: 1, secondDayOfMonth: 15).From(D, D.AddYears(1)).InCategory(Id1).WithCounterparty(null).Build());
        AssertRoundTrips(UpdateRecurringFlowRequestBuilder.A().WithAmount(9.99m).WithModel("Variable").Build());
        AssertRoundTrips(RecurringSuggestionDtoBuilder.A().Build());
    }

    [Fact]
    public void Asset_and_net_worth_types_round_trip()
    {
        var valuation = AssetValuationDtoBuilder.A().WithId(Id1).WithAsOf(D).WithRecordedAt(At).Build();
        AssetDto asset = AssetDtoBuilder.A().WithId(Id1).WithValuations([valuation]).Build();
        var worth = NetWorthDtoBuilder.A().WithByCurrency([CurrencyTotalDtoBuilder.A().Build()]).Build();

        AssertRoundTrips(asset);
        AssertRoundTrips(worth);
        AssertRoundTrips(valuation);
        AssertRoundTrips(AssetCategoryTemplateDtoBuilder.A().Build());
        AssertRoundTrips(AssetSummaryDtoBuilder.A()
                                     .WithByCategory([AssetCategoryBreakdownDtoBuilder.A().Build()])
                                     .WithTopAssets([asset])
                                     .WithNetWorth(worth)
                                     .Build());
        AssertRoundTrips(AssetRefreshResultDtoBuilder.A().WithAsset(asset).Build());
        AssertRoundTrips(CreateAssetRequestBuilder.A().Named("Car").InCategory("Vehicle").Worth(1m).ValuedOn(D).PurchasedOn(D, 1m).MadeBy(null, null)
            .Valued(null).WithAttributes(new Dictionary<string, string> { ["a"] = "b" }).Build());
        AssertRoundTrips(UpdateAssetRequestBuilder.A().Named("Car").InCategory("Vehicle").PurchasedOn(D, 1m).MadeBy(null, null).Valued("Manual", 0.1m).Build());
        AssertRoundTrips(RecordValuationRequestBuilder.A().Worth(5m).AsOf(D).From("Manual").WithNote("n").Build());
    }

    [Fact]
    public void Bill_sheet_types_round_trip()
    {
        var totals = SheetTotalsDtoBuilder.A()
                                 .WithEstimated(SheetTotalsLineBuilder.A().WithOptional(2m).WithRequired(3m).WithPersonal(4m).WithBusiness(5m).Build())
                                 .WithActual(SheetTotalsLineBuilder.A()
                                                                                             .WithOptional(1m)
                                                                                             .WithRequired(1m)
                                                                                             .WithPersonal(1m)
                                                                                             .WithBusiness(1m)
                                                                                             .Build())
                                 .Build();
        SheetRowDto row = SheetRowDtoBuilder.A().WithId(Id1).Build();
        var view = SheetViewDtoBuilder.A()
                               .WithSections([SheetSectionDtoBuilder.A().WithRows([row]).WithTotals(totals).Build()])
                               .WithGrandTotals(totals)
                               .Build();

        AssertRoundTrips(view);
        AssertRoundTrips(SaveBillRequestBuilder.A().InSection("Home").Named("Power").Perpetual().AutoPaying().DueOn(5).Estimating(90m).MatchingText("power")
            .Recurring(BillFrequency.EveryNWeeks, 2, D).Build());
        AssertRoundTrips(SetPaymentRequestBuilder.A().Paid().ForAmount(88m).InSlot(1).Build());
        AssertRoundTrips(ImportSheetRequestBuilder.A().OfCsv("a,b").Replacing().Build());
        AssertRoundTrips(SheetImportResultBuilder.A().WithSections(1).WithBills(2).Build());
        AssertRoundTrips(SheetRefreshResultBuilder.A().WithMarked(1).WithCleared(2).WithAdded(3).Build());
    }

    [Fact]
    public void Duplicate_import_sync_and_local_ai_types_round_trip()
    {
        var side = DuplicateSideDtoBuilder.A().WithId(Id1).WithAccountId(Id2).WithEffectiveDate(D).Build();
        AssertRoundTrips(CategoryRuleDtoBuilder.A().WithPattern("amzn").WithCategoryPath("Shopping.Online").Build());
        AssertRoundTrips(DuplicateSuggestionDtoBuilder.A().WithKeep(side).WithRemove(side with { Id = Id2 }).Build());
        AssertRoundTrips(ImportSummaryDtoBuilder.A().WithImportBatchId(Id1).WithRowErrors([RowErrorDtoBuilder.A().Build()]).Build());
        AssertRoundTrips(MerchantGroupDtoBuilder.A().WithMembers([MerchantOccurrenceBuilder.A().WithRaw("AMZN").WithCount(2).Build()]).Build());
        AssertRoundTrips(SimilarTransactionDtoBuilder.A().WithId(Id1).WithDate(D).Build());
        AssertRoundTrips(SyncResultBuilder.A()
                                     .WithAccountsUpserted(1)
                                     .WithTransactionsAdded(2)
                                     .WithTransactionsSkipped(3)
                                     .WithRecurringFlowsAdded(4)
                                     .Build());
    }
}
