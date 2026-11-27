namespace FlowLedger.Api.Tests.Security;

internal enum RouteKind
{
    /// <summary>Parameterless GET of tenant data: tenant B must never see tenant A's rows.</summary>
    List,

    /// <summary>Addressed by a row id: B uses A's real id and must not read or change it.</summary>
    ById,

    /// <summary>Keyed by a natural key in a raw-SQL store (budget category, rule pattern, alias pattern, goal id in the body).</summary>
    Keyed,

    /// <summary>No tenant data (probes, static catalogues, pure calculations, host-level files, signature-authenticated webhooks).</summary>
    Global,

    /// <summary>Known hole in the sweep: tenant-scoped but not probed yet. None remain (<c>No_route_is_left_as_a_Gap</c> keeps it that way); add a helper here before using it again.</summary>
    Gap,

    /// <summary>Tenant-scoped write or integration route probed with a real payload by <c>TenantIsolationWriteTests</c> (B calls it, A must be unaffected).</summary>
    Written,
}

internal sealed record RouteEntry(RouteKind Kind, string Note = "", string? Resource = null);

/// <summary>
/// One entry per route the API exposes. A new route fails <c>TenantIsolationMatrixTests</c> until it is listed here, which forces the
/// author to decide how tenant isolation for it is proven. Prefer List/ById/Keyed; use Gap only with a reason, and shrink it over time.
/// </summary>
internal static class TenantIsolationManifest
{
    private static RouteEntry List() => new(RouteKind.List);

    private static RouteEntry ById(string resource) => new(RouteKind.ById, Resource: resource);

    private static RouteEntry Keyed() => new(RouteKind.Keyed);

    private static RouteEntry Global(string why) => new(RouteKind.Global, why);

    private static RouteEntry Written() => new(RouteKind.Written);

    public static readonly IReadOnlyDictionary<string, RouteEntry> Routes = new Dictionary<string, RouteEntry>(StringComparer.Ordinal)
    {
        ["ANY /alive"] = Global("liveness probe"),
        ["ANY /health"] = Global("health probe"),
        ["ANY /ready"] = Global("readiness probe"),
        ["DELETE /api/accounts/{id:guid}"] = ById("Account"),
        ["DELETE /api/accounts/{id:guid}/connector"] = ById("Account"),
        ["DELETE /api/assets/{id:guid}"] = ById("Asset"),
        ["DELETE /api/budgets"] = Keyed(),
        ["DELETE /api/categories/{id:guid}"] = ById("Category"),
        ["DELETE /api/category-rules"] = Keyed(),
        ["DELETE /api/goals/{id:guid}"] = ById("Goal"),
        ["DELETE /api/integrations/{id:guid}"] = Written(),
        ["DELETE /api/local-ai/models/{id}"] = Global("host-level model files"),
        ["DELETE /api/merchant-aliases"] = Keyed(),
        ["DELETE /api/recurring-flows/{id:guid}"] = ById("RecurringFlow"),
        ["DELETE /api/reports/{id:guid}"] = ById("Report"),
        ["DELETE /api/sheet/bills/{id:guid}"] = ById("SheetBill"),
        ["DELETE /api/sheet/bills/{id:guid}/payments/{month}"] = ById("SheetBill"),
        ["DELETE /api/strategies/account-rates/{accountId:guid}"] = ById("Account"),
        ["DELETE /api/users/identities/{userId:guid}"] = Written(),
        ["GET /api/accounts"] = List(),
        ["GET /api/integrations/connections"] = List(),
        ["GET /api/accounts/connectors"] = List(),
        ["GET /api/accounts/{id:guid}"] = ById("Account"),
        ["GET /api/alerts"] = List(),
        ["GET /api/subscriptions"] = List(),
        ["GET /api/assets"] = List(),
        ["GET /api/assets/categories"] = Global("static category list"),
        ["GET /api/assets/summary"] = List(),
        ["GET /api/assets/{id:guid}"] = ById("Asset"),
        ["GET /api/budgets"] = List(),
        ["GET /api/categories"] = List(),
        ["GET /api/categories/{id:guid}"] = ById("Category"),
        ["GET /api/category-rules"] = List(),
        ["GET /api/dashboard/activity"] = List(),
        ["GET /api/dashboard/insights"] = List(),
        ["GET /api/data/tenants"] = Written(),
        ["POST /api/data/tenants/{tenantId:guid}/offboard"] = Written(),
        ["POST /api/data/reencrypt-secrets"] = Written(),
        ["POST /api/data/raw-archive/seal"] = Written(),
        ["GET /api/data/export"] = Written(),
        ["GET /api/data/status"] = Written(),
        ["GET /api/duplicates"] = List(),
        ["GET /api/transactions/processing-window"] = List(),
        ["GET /api/duplicates/log"] = List(),
        ["GET /api/environment"] = Global("host environment flags"),
        ["GET /api/forecast"] = List(),
        ["GET /api/goals"] = List(),
        ["GET /api/home-assistant"] = List(),
        ["GET /api/home-assistant/preview"] = List(),
        ["GET /api/imports/formats"] = Global("static list of import formats"),
        ["GET /api/imports/suggestions"] = List(),
        ["GET /api/insights/breakdown"] = List(),
        ["GET /api/integrations"] = List(),
        ["GET /api/integrations/connectors"] = Global("connector catalogue"),
        ["GET /api/integrations/connectors/{connectorId}/available-accounts"] = Written(),
        ["GET /api/integrations/provider"] = Global("provider kind, no rows"),
        ["GET /api/integrations/quiltt/config"] = Global("public connector config"),
        ["GET /api/integrations/{id:guid}/deliveries"] = Written(),
        ["GET /api/investments/performance"] = List(),
        ["GET /api/local-ai/merchant-groups"] = List(),
        ["GET /api/local-ai/similar/{transactionId:guid}"] = ById("Transaction"),
        ["GET /api/local-ai/status"] = Global("host-level local model status"),
        ["GET /api/merchant-aliases"] = List(),
        ["GET /api/query/schema"] = Global("static dataset schema"),
        ["GET /api/recurring-flows"] = List(),
        ["GET /api/recurring-flows/suggestions"] = List(),
        ["GET /api/recurring-flows/{id:guid}"] = ById("RecurringFlow"),
        ["GET /api/reports"] = List(),
        ["GET /api/reports/{id}"] = ById("Report"),
        ["GET /api/reports/{id}/run"] = ById("Report"),
        ["GET /api/sheet"] = List(),
        ["GET /api/status"] = Global("host status"),
        ["GET /api/strategies"] = List(),
        ["GET /api/strategies/account-rates"] = List(),
        ["GET /api/strategies/rates"] = Global("market rate defaults"),
        ["GET /api/sync/estimate"] = List(),
        ["GET /api/sync/status"] = List(),
        ["GET /api/transactions"] = List(),
        ["GET /api/transactions/export.csv"] = List(),
        ["GET /api/transactions/{id:guid}"] = ById("Transaction"),

        ["GET /api/transactions/{id:guid}/same-merchant-count"] = ById("Transaction"),
        ["GET /api/users/identities"] = List(),
        ["GET /openapi/{documentName}.json"] = Global("API description"),
        ["POST /api/accounts"] = Written(),
        // #189: acts only on the caller's own tenant (id from the tenant context, never the body); proven by SelfServiceErasureTests.
        ["POST /api/account/delete-my-data"] = Written(),
        ["POST /api/assets"] = Written(),
        ["POST /api/assets/{id:guid}/refresh"] = ById("Asset"),
        ["POST /api/assets/{id:guid}/valuations"] = ById("Asset"),
        ["POST /api/categories"] = Written(),
        ["POST /api/categories/defaults"] = Written(),
        ["POST /api/categories/move"] = Written(),
        ["POST /api/categorization/clear-auto"] = Written(),
        ["POST /api/categorization/run"] = Written(),
        ["POST /api/connect"] = Written(),
        ["POST /api/dashboard/suspicious/{transactionId:guid}/dismiss"] = ById("Transaction"),
        ["POST /api/data/backup-now"] = Written(),
        ["POST /api/data/import"] = Written(),
        ["POST /api/debt/plan"] = Global("pure calculation on the request body"),
        ["POST /api/duplicates/auto-merge"] = Written(),
        ["POST /api/duplicates/dismiss"] = Written(),
        ["POST /api/duplicates/merge"] = Written(),
        ["POST /api/home-assistant/send"] = Written(),
        ["POST /api/home-assistant/test"] = Written(),
        ["POST /api/imports"] = Written(),
        ["POST /api/imports/auto"] = Written(),
        ["POST /api/integrations"] = Written(),
        ["POST /api/integrations/mx/connect-token"] = Written(),
        ["POST /api/integrations/mx/webhooks"] = Global("authenticated by provider signature, tenant derived from payload (see MxEndpointTests)"),
        ["POST /api/integrations/quiltt/session"] = Written(),
        ["POST /api/integrations/quiltt/webhooks"] = Global("authenticated by provider signature (see Quiltt tests)"),
        ["POST /api/integrations/{id:guid}/rest/call"] = Written(),
        ["POST /api/integrations/{id:guid}/rotate-secret"] = Written(),
        ["POST /api/integrations/{id:guid}/soap/invoke"] = Written(),
        ["POST /api/integrations/{id:guid}/soap/operations"] = Written(),
        ["POST /api/integrations/{id:guid}/test"] = Written(),
        ["POST /api/local-ai/evaluate"] = Global("runs on supplied text only"),
        ["POST /api/local-ai/models/{id}/download"] = Global("host-level model files"),
        ["POST /api/merchant-aliases/seed-sheet-defaults"] = Written(),
        ["POST /api/query/ask"] = Written(),
        ["POST /api/query/run"] = Written(),
        ["POST /api/recurring-flows"] = Written(),
        ["POST /api/recurring-flows/detect"] = Written(),
        ["POST /api/recurring-flows/suggestions/accept"] = Written(),
        ["POST /api/recurring-flows/suggestions/dismiss"] = Written(),
        ["POST /api/recurring-flows/{id:guid}/flag-remove"] = ById("RecurringFlow"),
        ["POST /api/reports"] = Written(),
        ["POST /api/sheet/bills"] = Written(),
        ["POST /api/sheet/import"] = Written(),
        ["POST /api/sheet/refresh"] = Written(),
        ["POST /api/strategies/simulate/fire"] = Global("pure calculation on the request body"),
        ["POST /api/strategies/simulate/goal"] = Global("pure calculation on the request body"),
        ["POST /api/strategies/simulate/growth"] = Global("pure calculation on the request body"),
        ["POST /api/strategies/simulate/ladder"] = Global("pure calculation on the request body"),
        ["POST /api/strategies/simulate/past"] = Global("pure calculation on the request body"),
        ["POST /api/strategies/simulate/refinance"] = Global("pure calculation on the request body"),
        ["POST /api/sync"] = Written(),
        ["POST /api/transactions"] = Written(),
        ["POST /api/transactions/bulk-category"] = Written(),
        ["POST /api/transactions/enrich"] = Written(),

        ["POST /api/transactions/restore-categories"] = Written(),
        ["POST /api/transactions/{id:guid}/match"] = ById("Transaction"),
        ["POST /api/transactions/{id:guid}/unmatch"] = ById("Transaction"),
        ["POST /api/users/link"] = Written(),
        ["POST /api/users/link-code"] = Written(),
        ["POST /api/users/resolve"] = Global("identity to tenant mapping; API-key only (MultiUserTests)"),
        ["PUT /api/accounts/{id:guid}"] = ById("Account"),
        ["PUT /api/accounts/{id:guid}/connector"] = ById("Account"),
        ["PUT /api/accounts/{id:guid}/settings"] = ById("Account"),
        ["PUT /api/accounts/{id:guid}/type"] = ById("Account"),
        ["PUT /api/assets/{id:guid}"] = ById("Asset"),
        ["PUT /api/budgets"] = Keyed(),
        ["PUT /api/categories/{id:guid}"] = ById("Category"),
        ["PUT /api/category-rules"] = Keyed(),
        ["PUT /api/goals"] = Keyed(),
        ["PUT /api/home-assistant"] = Written(),
        ["PUT /api/integrations/{id:guid}"] = Written(),
        ["PUT /api/merchant-aliases"] = Keyed(),
        ["PUT /api/recurring-flows/{id:guid}"] = ById("RecurringFlow"),
        ["PUT /api/reports/{id:guid}"] = ById("Report"),
        ["PUT /api/sheet/bills/{id:guid}"] = ById("SheetBill"),
        ["PUT /api/sheet/bills/{id:guid}/payments/{month}"] = ById("SheetBill"),
        ["PUT /api/strategies/account-rates/{accountId:guid}"] = ById("Account"),
        ["PUT /api/transactions/{id:guid}/category"] = ById("Transaction"),
        ["PUT /api/transactions/{id:guid}/note"] = ById("Transaction"),
        ["PUT /api/transactions/{id:guid}/recategorize"] = ById("Transaction"),
        ["PUT /api/transactions/{id:guid}/splits"] = ById("Transaction"),
    };
}
