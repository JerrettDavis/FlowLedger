namespace FlowLedger.Architecture.Tests;

/// <summary>
/// #96: the Web client hand-copies API contract types. Until a shared Contracts project exists, no NEW public type name may be
/// defined in both Application and Web; existing duplicates sit on a shrink-only allow-list.
/// </summary>
public sealed class ContractDuplicationTests
{
    private static IEnumerable<string> PublicTypeNames(System.Reflection.Assembly a) =>
        a.GetExportedTypes().Where(t => !t.IsNested && !t.IsGenericTypeDefinition).Select(t => t.Name);

    private static readonly string[] Allowlist = [
        "AssetCategoryBreakdownDto",
        "AssetCategoryTemplateDto",
        "AssetDto",
        "AssetRefreshResultDto",
        "AssetSummaryDto",
        "AssetValuationDto",
        "CategoryRuleDto",
        "CreateAssetRequest",
        "CreateRecurringFlowRequest",
        "CreateTransactionRequest",
        "DashboardActivityDto",
        "DuplicateSideDto",
        "DuplicateSuggestionDto",
        "FlaggedTransactionDto",
        "ImportSheetRequest",
        "ImportSummaryDto",
        "MerchantGroupDto",
        "NetWorthDto",
        "PanelResultDto",
        "QuerySchemaDto",
        "RecordValuationRequest",
        "RecurringFlowDto",
        "RecurringSuggestionDto",
        "RunReportResultDto",
        "SaveBillRequest",
        "SavedReportDto",
        "SaveReportRequest",
        "SetPaymentRequest",
        "SheetCellDto",
        "SheetImportResult",
        "SheetMonthSummaryDto",
        "SheetRefreshResult",
        "SheetRowDto",
        "SheetSectionDto",
        "SheetSlotDto",
        "SheetTotalsDto",
        "SheetTotalsLine",
        "SheetViewDto",
        "SimilarTransactionDto",
        "SyncResult",
        "TransactionDto",
        "TransactionSplitDto",
        "UpdateAssetRequest",
        "UpdateRecurringFlowRequest",
        "ValuationSpreadDto",
    ];

    [Fact]
    public void No_new_public_type_name_is_defined_in_both_Application_and_Web()
    {
        var app = PublicTypeNames(typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly).ToHashSet();
        var web = PublicTypeNames(typeof(FlowLedger.Web.ApiClient.FlowLedgerApiClient).Assembly);

        var duplicates = web.Where(app.Contains).Distinct().Order().ToList();
        var fresh = duplicates.Except(Allowlist).ToList();
        var stale = Allowlist.Except(duplicates).ToList();
        Assert.True(stale.Count == 0, "Remove resolved duplicates from the allow-list: " + string.Join(", ", stale));

        Assert.True(fresh.Count == 0, "Duplicated across Application and Web (" + fresh.Count + "): " + string.Join(", ", fresh));
    }
}
