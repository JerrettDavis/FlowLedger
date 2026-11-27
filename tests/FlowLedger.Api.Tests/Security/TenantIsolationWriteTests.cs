using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Issue #136, second slice: the payload-driven writes and integration routes that the by-id sweep in
/// <see cref="TenantIsolationMatrixTests"/> cannot reach (<see cref="RouteKind.Written"/> in the manifest).
/// Tenant A is seeded; tenant B then calls each route, with A's real ids/keys in the payload where the route takes any, and
/// the call must (a) never fail with a server error, (b) return the expected status, (c) never echo A's data and
/// (d) leave everything tenant A can read byte-for-byte unchanged.
/// </summary>
[Collection("ApiIntegration")]
public sealed class TenantIsolationWriteTests(FlowLedgerApiFactory factory, Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private const string Secret = TenantIsolationMatrixTests.Secret;

    /// <summary>What tenant B's call needs: its own client, A's client (to read A's live state) and A's seeded ids.</summary>
    internal sealed record Ctx(HttpClient B, HttpClient A, TenantIsolationMatrixTests.Seeded Seed, string SuggestionKey, DateOnly ServerMonth);

    internal sealed record WriteCase(Func<Ctx, Task<HttpResponseMessage>> Send, string? BodyMustContain, params HttpStatusCode[] Expect);

    private static WriteCase Case(Func<Ctx, Task<HttpResponseMessage>> send, params HttpStatusCode[] expect) => new(send, null, expect);

    private static WriteCase CaseWithBody(Func<Ctx, Task<HttpResponseMessage>> send, string bodyMustContain, params HttpStatusCode[] expect) => new(send, bodyMustContain, expect);

    private static Task<HttpResponseMessage> Json(HttpClient c, HttpMethod method, string url, object? body = null)
        => c.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });

    private static string SheetCsv(DateOnly month)
    {
        var prev = month.AddMonths(-1).ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        var cur = month.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        return $" ,Payer,Debt,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost,{prev},{cur}\n" +
               "B Power,JD,No,Yes,No,Personal,Electric,Auto,123-456-7890,4.0,500,480.5,x,\n" +
               "Total,,,,,,,,,,500,480.5\n";
    }

    private static readonly System.Text.RegularExpressions.Regex Timestamp =
        new(@"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Cheap per-case view of tenant A (the full <see cref="TenantIsolationMatrixTests.SnapshotAsync"/> is run before and after all cases):
    /// the global per-IP rate limit makes a full snapshot per case too slow.</summary>
    private static async Task<string> FingerprintAsync(HttpClient a, TenantIsolationMatrixTests.Seeded seed)
    {
        string[] urls =
        [
            "/api/accounts", "/api/transactions", "/api/categories", "/api/recurring-flows", "/api/recurring-flows/suggestions", "/api/assets",
            "/api/reports", "/api/integrations", $"/api/integrations/{seed.Integration}/deliveries", "/api/duplicates", "/api/home-assistant",
            "/api/sheet", "/api/goals", "/api/budgets", "/api/category-rules", "/api/merchant-aliases", "/api/users/identities",
        ];
        var sb = new System.Text.StringBuilder();
        foreach (var url in urls)
        {
            var r = await a.GetAsync(url);
            sb.Append(url).Append(' ').Append((int)r.StatusCode).Append('\n').Append(Timestamp.Replace(await r.Content.ReadAsStringAsync(), "<ts>")).Append('\n');
        }

        return sb.ToString();
    }

    private static object IntegrationBody(string name = "B Hook") => new
    {
        name,
        kind = "Rest",
        enabled = true,
        url = "https://api.example.com/v1",
        auth = "None",
        authUser = (string?)null,
        credential = (string?)null,
        clearCredential = false,
        headers = (object?)null,
        events = (string[]?)null,
        soapEndpoint = (string?)null,
    };

    /// <summary>The probes. Keys are manifest route keys; <see cref="DedicatedFacts"/> lists the ones with their own test below.</summary>
    internal static readonly IReadOnlyDictionary<string, WriteCase> Cases = new Dictionary<string, WriteCase>(StringComparer.Ordinal)
    {
        // ── Writes that create or act on the caller's own tenant: B's own data must never appear in A's view. ──
        ["POST /api/accounts"] = Case(c => Json(c.B, HttpMethod.Post, "/api/accounts", CreateAccountRequestBuilder.A().Named("B Checking").OfType("Checking").StartingAt(5m).Build()), HttpStatusCode.Created),
        ["POST /api/assets"] = Case(c => Json(c.B, HttpMethod.Post, "/api/assets",
            new CreateAssetRequest("B Laptop", "Electronics", 10m, "USD", null, new DateOnly(2024, 1, 1), 12m, "Apple", "MBP", null, null, "Manual", null, null, null, null, null)), HttpStatusCode.Created),
        // Same path as A's category: per-tenant uniqueness, so B must be able to create it and A must still have exactly one.
        ["POST /api/categories"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categories", CreateCategoryRequestBuilder.A().AtPath("zz-a-secret").Named("B Category").Build()), HttpStatusCode.Created),
        ["POST /api/categories/defaults"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categories/defaults"), HttpStatusCode.OK),
        ["POST /api/categorization/clear-auto"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categorization/clear-auto"), HttpStatusCode.OK),
        ["POST /api/categorization/run"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categorization/run"), HttpStatusCode.OK),
        ["POST /api/transactions/enrich"] = Case(c => Json(c.B, HttpMethod.Post, "/api/transactions/enrich"), HttpStatusCode.OK),
        ["POST /api/duplicates/auto-merge"] = Case(c => Json(c.B, HttpMethod.Post, "/api/duplicates/auto-merge"), HttpStatusCode.OK),
        ["POST /api/merchant-aliases/seed-sheet-defaults"] = Case(c => Json(c.B, HttpMethod.Post, "/api/merchant-aliases/seed-sheet-defaults"), HttpStatusCode.OK),
        ["POST /api/recurring-flows/detect"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows/detect"), HttpStatusCode.OK),
        ["POST /api/sheet/refresh"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sheet/refresh"), HttpStatusCode.OK, HttpStatusCode.NoContent),
        ["POST /api/sheet/bills"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sheet/bills",
            new SaveBillRequest("B", "B Bill", null, false, false, false, false, false, 4, 100m, null, null, null, null)), HttpStatusCode.OK, HttpStatusCode.Created),
        // Replace=true wipes the CALLER's sheet; it must not wipe A's bill. A real sheet (current and previous month columns), so it must succeed.
        ["POST /api/sheet/import"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sheet/import", ImportSheetRequestBuilder.A().OfCsv(SheetCsv(c.ServerMonth)).Replacing().Build()), HttpStatusCode.OK),
        ["POST /api/reports"] = Case(c => Json(c.B, HttpMethod.Post, "/api/reports",
            new SaveReportRequest("B Report", "d", "View", [new ReportPanel("p", "transactions | summarize t = sum(amount) by merchant", new VizSpec("column", "merchant", ["t"]))])), HttpStatusCode.Created, HttpStatusCode.OK),
        ["POST /api/query/run"] = Case(c => Json(c.B, HttpMethod.Post, "/api/query/run", new { query = "transactions | take 100" }), HttpStatusCode.OK),
        ["POST /api/connect"] = Case(c => Json(c.B, HttpMethod.Post, "/api/connect"), HttpStatusCode.OK),
        ["POST /api/sync"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sync"), HttpStatusCode.OK),
        ["POST /api/integrations"] = Case(c => Json(c.B, HttpMethod.Post, "/api/integrations", IntegrationBody()), HttpStatusCode.Created),
        ["PUT /api/home-assistant"] = Case(c => Json(c.B, HttpMethod.Put, "/api/home-assistant",
            new { enabled = false, webhookUrl = (string?)null, clearWebhook = false, shareAmounts = true, includeNames = true, leadDays = 9, kinds = (string[]?)null }), HttpStatusCode.OK),
        ["POST /api/home-assistant/send"] = Case(c => Json(c.B, HttpMethod.Post, "/api/home-assistant/send"), HttpStatusCode.OK),
        ["POST /api/home-assistant/test"] = CaseWithBody(c => Json(c.B, HttpMethod.Post, "/api/home-assistant/test"), "Save a webhook URL first", HttpStatusCode.BadGateway),

        // ── Payloads that name A's rows: B must be refused or find nothing. ──
        ["POST /api/transactions"] = Case(c => Json(c.B, HttpMethod.Post, "/api/transactions",
            CreateTransactionRequestBuilder.A().OnAccount(c.Seed.Account).WithAmount(1m).WithDirection("Debit").Described("B INTRUDER").On(new DateOnly(2026, 1, 16)).Build()), HttpStatusCode.NotFound),
        ["POST /api/transactions/bulk-category"] = CaseWithBody(async c =>
        {
            using var category = await c.B.PostAsJsonAsync("/api/categories", CreateCategoryRequestBuilder.A().AtPath("b-bulk").Named("B Bulk").Build());
            category.EnsureSuccessStatusCode();
            var categoryId = (await category.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            return await Json(c.B, HttpMethod.Post, "/api/transactions/bulk-category",
                new { transactionIds = new[] { c.Seed.Transaction, c.Seed.Transaction2 }, categoryId });
        }, "\"changed\":0", HttpStatusCode.OK),
        ["POST /api/recurring-flows"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows",
            new CreateRecurringFlowRequest(c.Seed.Account, "B INTRUDER", 1m, "USD", "Debit", "Fixed", "Monthly", 1, null, null, null, new DateOnly(2026, 1, 1), null, null, "B")), HttpStatusCode.NotFound),
        ["POST /api/imports"] = Case(c => Json(c.B, HttpMethod.Post, "/api/imports",
            new { accountId = c.Seed.Account, csvContent = "Date,Amount,Description\n2026-01-20,-5.00,B INTRUDER", hasHeaderRow = true }), HttpStatusCode.UnprocessableEntity),
        ["POST /api/imports/auto"] = Case(c => Json(c.B, HttpMethod.Post, "/api/imports/auto",
            new { accountId = c.Seed.Account, content = "Date,Amount,Description\n2026-01-20,-5.00,B INTRUDER" }), HttpStatusCode.UnprocessableEntity),
        ["POST /api/categories/move"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categories/move", new { path = "zz-a-secret", newPath = "hijacked", dryRun = false }), HttpStatusCode.NotFound),
        ["POST /api/duplicates/merge"] = Case(c => Json(c.B, HttpMethod.Post, "/api/duplicates/merge",
            new { firstId = c.Seed.Transaction, secondId = c.Seed.Transaction2, keepId = c.Seed.Transaction }), HttpStatusCode.NotFound),
        ["POST /api/duplicates/dismiss"] = Case(c => Json(c.B, HttpMethod.Post, "/api/duplicates/dismiss",
            new { firstId = c.Seed.Transaction, secondId = c.Seed.Transaction2 }), HttpStatusCode.NoContent),
        ["POST /api/recurring-flows/suggestions/accept"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows/suggestions/accept", new { key = c.SuggestionKey }), HttpStatusCode.NotFound),
        ["POST /api/recurring-flows/suggestions/dismiss"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows/suggestions/dismiss", new { key = c.SuggestionKey }), HttpStatusCode.NoContent),

        // ── Integrations: A's real integration id, real bodies. The service reports an unknown id as 404 or 422, never as success. ──
        ["PUT /api/integrations/{id:guid}"] = CaseWithBody(c => Json(c.B, HttpMethod.Put, $"/api/integrations/{c.Seed.Integration}", IntegrationBody("HIJACKED")), "no longer exists", HttpStatusCode.BadRequest),
        ["DELETE /api/integrations/{id:guid}"] = Case(c => Json(c.B, HttpMethod.Delete, $"/api/integrations/{c.Seed.Integration}"), HttpStatusCode.NotFound),
        // An unknown id is an empty log, not A's log (A's has an entry: see the seeding in the test).
        ["GET /api/integrations/{id:guid}/deliveries"] = CaseWithBody(c => Json(c.B, HttpMethod.Get, $"/api/integrations/{c.Seed.Integration}/deliveries"), "[]", HttpStatusCode.OK),
        ["POST /api/integrations/{id:guid}/rotate-secret"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/rotate-secret"), HttpStatusCode.UnprocessableEntity),
        ["POST /api/integrations/{id:guid}/test"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/test"), HttpStatusCode.UnprocessableEntity),
        ["POST /api/integrations/{id:guid}/rest/call"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/rest/call", new { method = "GET", path = "/ping" }), HttpStatusCode.UnprocessableEntity),
        ["POST /api/integrations/{id:guid}/soap/operations"] = CaseWithBody(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/soap/operations"), "doesn't exist", HttpStatusCode.OK),
        ["POST /api/integrations/{id:guid}/soap/invoke"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/soap/invoke", new { operation = "Op", bodyXml = "<a/>" }), HttpStatusCode.UnprocessableEntity),

        // ── Host-level data administration spans every tenant: only the household tenant may call it. ──
        ["GET /api/data/export"] = Case(c => Json(c.B, HttpMethod.Get, "/api/data/export"), HttpStatusCode.Forbidden),
        ["GET /api/data/status"] = Case(c => Json(c.B, HttpMethod.Get, "/api/data/status"), HttpStatusCode.Forbidden),
        ["POST /api/data/backup-now"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/backup-now"), HttpStatusCode.Forbidden),
        ["POST /api/data/import"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/import"), HttpStatusCode.Forbidden),
        ["GET /api/data/tenants"] = Case(c => Json(c.B, HttpMethod.Get, "/api/data/tenants"), HttpStatusCode.Forbidden),
        ["POST /api/data/tenants/{tenantId:guid}/offboard"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/data/tenants/{TenantIsolationMatrixTests.TenantA}/offboard",
            new { confirmTenantId = TenantIsolationMatrixTests.TenantA }), HttpStatusCode.Forbidden),
        ["POST /api/data/reencrypt-secrets"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/reencrypt-secrets"), HttpStatusCode.Forbidden),
        ["POST /api/data/raw-archive/seal"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/raw-archive/seal"), HttpStatusCode.Forbidden),
        ["POST /api/integrations/mx/connect-token"] = Case(c => Json(c.B, HttpMethod.Post, "/api/integrations/mx/connect-token"), HttpStatusCode.Forbidden),
    };

    /// <summary>Written routes with their own [Fact] below (they need real users, not the fixed tenant guids).</summary>
    internal static readonly IReadOnlySet<string> DedicatedFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/users/link",
        "POST /api/users/link-code",
        "DELETE /api/users/identities/{userId:guid}",
        // Need an enabled connector, a stubbed Quiltt API and a fake model: see TenantIsolationConnectorProbeTests (own host).
        "GET /api/integrations/connectors/{connectorId}/available-accounts",
        "POST /api/integrations/quiltt/session",
        "POST /api/query/ask",
    };

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Every_Written_route_in_the_manifest_has_a_probe_and_every_probe_is_in_the_manifest()
    {
        var written = TenantIsolationManifest.Routes.Where(kv => kv.Value.Kind == RouteKind.Written).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var probed = Cases.Keys.Concat(DedicatedFacts).ToHashSet(StringComparer.Ordinal);

        written.Except(probed).Should().BeEmpty("a route marked Written must have a probe here");
        probed.Except(written).Should().BeEmpty("a probe must correspond to a Written manifest entry");
    }

    [Fact]
    public async Task Tenant_B_cannot_reach_or_change_tenant_A_through_any_written_route()
    {
        var a = factory.CreateAuthenticatedClient(TenantIsolationMatrixTests.TenantA);
        var seed = await TenantIsolationMatrixTests.SeedTenantAAsync(a);

        // "This month" as the host sees it (GET /api/budgets reports it), so nothing here reads the test's own clock. The recurring
        // detector and the bill sheet judge recency against the host's clock.
        using var budgets = JsonDocument.Parse(await a.GetStringAsync("/api/budgets"));
        var month = DateOnly.ParseExact(budgets.RootElement.GetProperty("month").GetString()! + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // A real recurring pattern for A, so the suggestion key B replays is one A could actually accept.
        for (var back = 1; back <= 5; back++)
        {
            var d = month.AddMonths(-back);
            (await a.PostAsJsonAsync("/api/transactions",
                CreateTransactionRequestBuilder.A().OnAccount(seed.Account).WithAmount(29.99m).WithDirection("Debit").Described($"{Secret} Power Company").On(new DateOnly(d.Year, d.Month, 5)).PostedOn(new DateOnly(d.Year, d.Month, 5)).Build()))
                .IsSuccessStatusCode.Should().BeTrue();
        }

        var suggestions = JsonDocument.Parse(await a.GetStringAsync("/api/recurring-flows/suggestions")).RootElement;
        suggestions.ValueKind.Should().Be(JsonValueKind.Array);
        suggestions.GetArrayLength().Should().BeGreaterThan(0, "tenant A must really have a suggestion for the replay probes to mean anything");
        var key = suggestions[0].GetProperty("key").GetString()!;

        // One logged delivery for A's integration (the host does not resolve), so an empty log for B is distinguishable from A's.
        (await a.PostAsJsonAsync($"/api/integrations/{seed.Integration}/rest/call", new { method = "GET", path = "/ping" })).StatusCode.Should().Be(HttpStatusCode.OK);

        // A has a webhook configured, so B's "no webhook" answer from the Home Assistant routes is B's own state, not A's.
        (await a.PutAsJsonAsync("/api/home-assistant",
            new { enabled = false, webhookUrl = "https://ha.example.com/api/webhook/a-secret-hook-0123456789abcdef0123456789abcdef", clearWebhook = false, shareAmounts = false, includeNames = false, leadDays = 3, kinds = (string[]?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var before = await TenantIsolationMatrixTests.SnapshotAsync(a, seed);
        before.Should().Contain(Secret);
        var fingerprint = await FingerprintAsync(a, seed);
        var problems = new List<string>();

        foreach (var (route, probe) in Cases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // A fresh tenant per probe: B's own earlier writes (a category at the same path, a webhook) must not change what the next probe means.
            var b = factory.CreateAuthenticatedClient(Guid.NewGuid());
            using var response = await probe.Send(new Ctx(b, a, seed, key, month));
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"{route} -> {(int)response.StatusCode}");

            if (probe.Expect.Length > 0 ? !probe.Expect.Contains(response.StatusCode) : (int)response.StatusCode >= 500)
            {
                problems.Add($"{route} as B returned {(int)response.StatusCode} (expected {(probe.Expect.Length > 0 ? string.Join('/', probe.Expect.Select(e => (int)e)) : "non-5xx")}): {body}");
            }

            if (probe.BodyMustContain is not null && !body.Contains(probe.BodyMustContain, StringComparison.Ordinal))
            {
                problems.Add($"{route} as B: body should contain '{probe.BodyMustContain}': {body}");
            }

            if (body.Contains(Secret, StringComparison.Ordinal) || body.Contains("nonexistent.invalid", StringComparison.Ordinal) || body.Contains("a-secret-hook-0123456789abcdef0123456789abcdef", StringComparison.Ordinal))
            {
                problems.Add($"{route} as B leaked tenant A data: {body}");
            }

            var now = await FingerprintAsync(a, seed);
            if (now != fingerprint)
            {
                problems.Add($"{route} as B changed what tenant A can read");
                fingerprint = now; // report each offending probe once
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
        var after = await TenantIsolationMatrixTests.SnapshotAsync(a, seed);
        after.Should().Be(before, "nothing tenant B did may change tenant A's data");
        after.Should().NotContain("B INTRUDER").And.NotContain("HIJACKED");
    }

    // ── Users: identities are resolved from real issuers, so these cannot use the fixed tenant guids. ──

    private sealed record Resolved(Guid UserId, Guid TenantId, bool Created);

    private async Task<Resolved> ResolveAsync(string issuer, string subject)
    {
        var resp = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/users/resolve",
            new { issuer, subject, email = (string?)null, displayName = subject, emailVerified = false });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<Resolved>())!;
    }

    [Fact]
    public async Task Tenant_B_cannot_unlink_a_login_of_tenant_A()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = await ResolveAsync(issuer, "alice");
        var bob = await ResolveAsync(issuer, "bob");
        var aliceClient = factory.CreateAuthenticatedClient(alice.TenantId);
        var before = await aliceClient.GetStringAsync("/api/users/identities");
        before.Should().Contain(alice.UserId.ToString());

        var attack = await factory.CreateAuthenticatedClient(bob.TenantId).DeleteAsync($"/api/users/identities/{alice.UserId}");

        attack.StatusCode.Should().Be(HttpStatusCode.Conflict, "an unknown (to B) login is refused and, either way, not detached");
        (await aliceClient.GetStringAsync("/api/users/identities")).Should().Be(before);
    }

    [Fact]
    public async Task Tenant_B_cannot_redeem_a_link_code_on_behalf_of_a_login_of_tenant_A()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = await ResolveAsync(issuer, "alice");
        var bob = await ResolveAsync(issuer, "bob");
        var carol = await ResolveAsync(issuer, "carol");
        var carolClient = factory.CreateAuthenticatedClient(carol.TenantId);
        var code = (await (await carolClient.PostAsync("/api/users/link-code", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

        // Bob names Alice's login while holding Carol's code: Alice's login does not belong to Bob's tenant.
        var attack = await factory.CreateAuthenticatedClient(bob.TenantId).PostAsJsonAsync("/api/users/link", new { userId = alice.UserId, code });

        attack.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ResolveAsync(issuer, "alice")).TenantId.Should().Be(alice.TenantId, "Alice must not be moved into Carol's tenant");
        (await carolClient.GetStringAsync("/api/users/identities")).Should().NotContain(alice.UserId.ToString());
    }

    [Fact]
    public async Task A_link_code_only_ever_links_into_the_tenant_that_issued_it()
    {
        var issuer = "https://idp.test/" + Guid.NewGuid();
        var alice = await ResolveAsync(issuer, "alice");
        var bob = await ResolveAsync(issuer, "bob");
        var carol = await ResolveAsync(issuer, "carol");
        var bobClient = factory.CreateAuthenticatedClient(bob.TenantId);
        var aliceBefore = await factory.CreateAuthenticatedClient(alice.TenantId).GetStringAsync("/api/users/identities");

        var code = (await (await bobClient.PostAsync("/api/users/link-code", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        var redeem = await factory.CreateAuthenticatedClient(carol.TenantId).PostAsJsonAsync("/api/users/link", new { userId = carol.UserId, code });

        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        (await bobClient.GetStringAsync("/api/users/identities")).Should().Contain(carol.UserId.ToString());
        (await factory.CreateAuthenticatedClient(alice.TenantId).GetStringAsync("/api/users/identities")).Should().Be(aliceBefore, "Bob's code must not touch Alice's tenant");
    }
}
