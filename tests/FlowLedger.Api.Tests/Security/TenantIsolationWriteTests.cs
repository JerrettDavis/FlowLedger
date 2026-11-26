using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
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
public sealed class TenantIsolationWriteTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private const string Secret = TenantIsolationMatrixTests.Secret;

    /// <summary>What tenant B's call needs: its own client, A's client (to read A's live state) and A's seeded ids.</summary>
    internal sealed record Ctx(HttpClient B, HttpClient A, TenantIsolationMatrixTests.Seeded Seed, string SuggestionKey);

    internal sealed record WriteCase(Func<Ctx, Task<HttpResponseMessage>> Send, string? BodyMustContain, params HttpStatusCode[] Expect);

    private static readonly HttpStatusCode[] Rejected =
        [HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity, HttpStatusCode.Forbidden, HttpStatusCode.Conflict];

    private static WriteCase Case(Func<Ctx, Task<HttpResponseMessage>> send, params HttpStatusCode[] expect) => new(send, null, expect);

    private static WriteCase CaseWithBody(Func<Ctx, Task<HttpResponseMessage>> send, string bodyMustContain, params HttpStatusCode[] expect) => new(send, bodyMustContain, expect);

    private static Task<HttpResponseMessage> Json(HttpClient c, HttpMethod method, string url, object? body = null)
        => c.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });

    private static object IntegrationBody(string name = "B Hook") => new
    {
        name, kind = "Rest", enabled = true, url = "https://api.example.com/v1", auth = "None",
        authUser = (string?)null, credential = (string?)null, clearCredential = false, headers = (object?)null, events = (string[]?)null, soapEndpoint = (string?)null,
    };

    /// <summary>The probes. Keys are manifest route keys; <see cref="DedicatedFacts"/> lists the ones with their own test below.</summary>
    internal static readonly IReadOnlyDictionary<string, WriteCase> Cases = new Dictionary<string, WriteCase>(StringComparer.Ordinal)
    {
        // ── Writes that create or act on the caller's own tenant: B's own data must never appear in A's view. ──
        ["POST /api/accounts"] = Case(c => Json(c.B, HttpMethod.Post, "/api/accounts", new CreateAccountRequest("B Checking", "Checking", 5m, "USD", null, null)), HttpStatusCode.Created),
        ["POST /api/assets"] = Case(c => Json(c.B, HttpMethod.Post, "/api/assets",
            new CreateAssetRequest("B Laptop", "Electronics", 10m, "USD", null, new DateOnly(2024, 1, 1), 12m, "Apple", "MBP", null, null, "Manual", null, null, null, null, null)), HttpStatusCode.Created),
        // Same path as A's category: per-tenant uniqueness, so B must be able to create it and A must still have exactly one.
        ["POST /api/categories"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categories", new CreateCategoryRequest("zz-a-secret", "B Category", null)), HttpStatusCode.Created),
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
        // Replace=true wipes the CALLER's sheet; it must not wipe A's bill.
        ["POST /api/sheet/import"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sheet/import", new ImportSheetRequest("Name,Amount\nB Bill,10", true)), HttpStatusCode.OK, HttpStatusCode.BadRequest),
        ["POST /api/reports"] = Case(c => Json(c.B, HttpMethod.Post, "/api/reports",
            new SaveReportRequest("B Report", "d", "View", [new ReportPanel("p", "transactions | summarize t = sum(amount) by merchant", new VizSpec("column", "merchant", ["t"]))])), HttpStatusCode.Created, HttpStatusCode.OK),
        ["POST /api/query/run"] = Case(c => Json(c.B, HttpMethod.Post, "/api/query/run", new { query = "transactions | take 100" }), HttpStatusCode.OK),
        ["POST /api/connect"] = Case(c => Json(c.B, HttpMethod.Post, "/api/connect"), HttpStatusCode.OK),
        ["POST /api/sync"] = Case(c => Json(c.B, HttpMethod.Post, "/api/sync"), HttpStatusCode.OK),
        ["POST /api/integrations"] = Case(c => Json(c.B, HttpMethod.Post, "/api/integrations", IntegrationBody()), HttpStatusCode.Created),
        ["PUT /api/home-assistant"] = Case(c => Json(c.B, HttpMethod.Put, "/api/home-assistant",
            new { enabled = false, webhookUrl = (string?)null, clearWebhook = false, shareAmounts = true, includeNames = true, leadDays = 9, kinds = (string[]?)null }), HttpStatusCode.OK),
        ["POST /api/home-assistant/send"] = Case(c => Json(c.B, HttpMethod.Post, "/api/home-assistant/send"), HttpStatusCode.OK),
        ["POST /api/home-assistant/test"] = Case(c => Json(c.B, HttpMethod.Post, "/api/home-assistant/test"), HttpStatusCode.BadGateway, HttpStatusCode.OK),

        // ── Payloads that name A's rows: B must be refused or find nothing. ──
        ["POST /api/transactions"] = Case(c => Json(c.B, HttpMethod.Post, "/api/transactions",
            new CreateTransactionRequest(c.Seed.Account, 1m, "USD", "Debit", "B INTRUDER", new DateOnly(2026, 1, 16), null, null, null, null)), Rejected),
        ["POST /api/recurring-flows"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows",
            new CreateRecurringFlowRequest(c.Seed.Account, "B INTRUDER", 1m, "USD", "Debit", "Fixed", "Monthly", 1, null, null, null, new DateOnly(2026, 1, 1), null, null, "B")), Rejected),
        ["POST /api/imports"] = Case(c => Json(c.B, HttpMethod.Post, "/api/imports",
            new { accountId = c.Seed.Account, csvContent = "Date,Amount,Description\n2026-01-20,-5.00,B INTRUDER", hasHeaderRow = true }), Rejected),
        ["POST /api/imports/auto"] = Case(c => Json(c.B, HttpMethod.Post, "/api/imports/auto",
            new { accountId = c.Seed.Account, content = "Date,Amount,Description\n2026-01-20,-5.00,B INTRUDER" }), Rejected),
        ["POST /api/categories/move"] = Case(c => Json(c.B, HttpMethod.Post, "/api/categories/move", new { path = "zz-a-secret", newPath = "hijacked", dryRun = false }), Rejected),
        ["POST /api/duplicates/merge"] = Case(c => Json(c.B, HttpMethod.Post, "/api/duplicates/merge",
            new { firstId = c.Seed.Transaction, secondId = c.Seed.Transaction2, keepId = c.Seed.Transaction }), Rejected),
        ["POST /api/duplicates/dismiss"] = Case(c => Json(c.B, HttpMethod.Post, "/api/duplicates/dismiss",
            new { firstId = c.Seed.Transaction, secondId = c.Seed.Transaction2 }), HttpStatusCode.NoContent),
        ["POST /api/recurring-flows/suggestions/accept"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows/suggestions/accept", new { key = c.SuggestionKey }), HttpStatusCode.NotFound),
        ["POST /api/recurring-flows/suggestions/dismiss"] = Case(c => Json(c.B, HttpMethod.Post, "/api/recurring-flows/suggestions/dismiss", new { key = c.SuggestionKey }), HttpStatusCode.NoContent),

        // ── Integrations: A's real integration id, real bodies. The service reports an unknown id as 404 or 422, never as success. ──
        ["PUT /api/integrations/{id:guid}"] = Case(c => Json(c.B, HttpMethod.Put, $"/api/integrations/{c.Seed.Integration}", IntegrationBody("HIJACKED")), Rejected),
        ["DELETE /api/integrations/{id:guid}"] = Case(c => Json(c.B, HttpMethod.Delete, $"/api/integrations/{c.Seed.Integration}"), Rejected),
        // An unknown id is an empty log, not A's log (A's has an entry: see the seeding in the test).
        ["GET /api/integrations/{id:guid}/deliveries"] = CaseWithBody(c => Json(c.B, HttpMethod.Get, $"/api/integrations/{c.Seed.Integration}/deliveries"), "[]", HttpStatusCode.OK),
        ["POST /api/integrations/{id:guid}/rotate-secret"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/rotate-secret"), Rejected),
        ["POST /api/integrations/{id:guid}/test"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/test"), Rejected),
        ["POST /api/integrations/{id:guid}/rest/call"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/rest/call", new { method = "GET", path = "/ping" }), Rejected),
        ["POST /api/integrations/{id:guid}/soap/operations"] = CaseWithBody(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/soap/operations"), "doesn't exist", HttpStatusCode.OK),
        ["POST /api/integrations/{id:guid}/soap/invoke"] = Case(c => Json(c.B, HttpMethod.Post, $"/api/integrations/{c.Seed.Integration}/soap/invoke", new { operation = "Op", bodyXml = "<a/>" }), Rejected),

        // ── Host-level data administration spans every tenant: only the household tenant may call it. ──
        ["GET /api/data/export"] = Case(c => Json(c.B, HttpMethod.Get, "/api/data/export"), HttpStatusCode.Forbidden),
        ["GET /api/data/status"] = Case(c => Json(c.B, HttpMethod.Get, "/api/data/status"), HttpStatusCode.Forbidden),
        ["POST /api/data/backup-now"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/backup-now"), HttpStatusCode.Forbidden),
        ["POST /api/data/import"] = Case(c => Json(c.B, HttpMethod.Post, "/api/data/import"), HttpStatusCode.Forbidden),
        ["POST /api/integrations/mx/connect-token"] = Case(c => Json(c.B, HttpMethod.Post, "/api/integrations/mx/connect-token"), HttpStatusCode.Forbidden),
    };

    /// <summary>Written routes with their own [Fact] below (they need real users, not the fixed tenant guids).</summary>
    internal static readonly IReadOnlySet<string> DedicatedFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/users/link",
        "POST /api/users/link-code",
        "DELETE /api/users/identities/{userId:guid}",
    };

    public static TheoryData<string> CaseKeys() => new(Cases.Keys.Order(StringComparer.Ordinal));

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

    [Theory]
    [MemberData(nameof(CaseKeys))]
    public async Task Tenant_B_cannot_reach_or_change_tenant_A_through(string route)
    {
        var probe = Cases[route];
        var a = factory.CreateAuthenticatedClient(TenantIsolationMatrixTests.TenantA);
        var b = factory.CreateAuthenticatedClient(TenantIsolationMatrixTests.TenantB);
        var seed = await TenantIsolationMatrixTests.SeedTenantAAsync(a);

        // A real recurring pattern for A, so the suggestion key B replays is one A could actually accept.
        // The detector judges recency against the server's clock, so take "today" from the server's Date header rather than the test's own clock.
        using var statusResponse = await a.GetAsync("/api/status");
        var serverToday = DateOnly.FromDateTime(statusResponse.Headers.Date!.Value.UtcDateTime);
        for (var back = 1; back <= 5; back++)
        {
            var month = serverToday.AddMonths(-back);
            (await a.PostAsJsonAsync("/api/transactions",
                new CreateTransactionRequest(seed.Account, 29.99m, "USD", "Debit", $"{Secret} Gym", new DateOnly(month.Year, month.Month, 5), null, null, null, null)))
                .IsSuccessStatusCode.Should().BeTrue();
        }

        // One logged delivery for A's integration (the host does not resolve), so an empty log for B is distinguishable from A's.
        (await a.PostAsJsonAsync($"/api/integrations/{seed.Integration}/rest/call", new { method = "GET", path = "/ping" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var suggestions = JsonDocument.Parse(await a.GetStringAsync("/api/recurring-flows/suggestions")).RootElement;
        var key = suggestions.ValueKind == JsonValueKind.Array && suggestions.GetArrayLength() > 0
            ? suggestions[0].GetProperty("key").GetString()!
            : "no-suggestion-seeded";
        if (route.Contains("suggestions", StringComparison.Ordinal))
        {
            key.Should().NotBe("no-suggestion-seeded", "tenant A must really have a suggestion for this probe to mean anything");
        }

        var before = await TenantIsolationMatrixTests.SnapshotAsync(a, seed);
        before.Should().Contain(Secret);

        using var response = await probe.Send(new Ctx(b, a, seed, key));
        var body = await response.Content.ReadAsStringAsync();

        if (probe.Expect.Length > 0)
        {
            probe.Expect.Should().Contain(response.StatusCode, $"{route} as B returned {(int)response.StatusCode}: {body}");
        }
        else
        {
            ((int)response.StatusCode).Should().BeLessThan(500, $"{route} as B: {response.StatusCode} {body}");
        }

        if (probe.BodyMustContain is not null)
        {
            body.Should().Contain(probe.BodyMustContain);
        }

        body.Should().NotContain(Secret, $"{route} as B must not echo tenant A's data").And.NotContain("nonexistent.invalid", "A's delivery log must not reach B");
        var after = await TenantIsolationMatrixTests.SnapshotAsync(a, seed);
        after.Should().Be(before, $"{route} as B must not change anything tenant A can read");
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
