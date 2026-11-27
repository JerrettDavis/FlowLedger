using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Issue #136, first slice: a route-driven tenant isolation sweep.
///  1. Every route the host exposes must have an entry in <see cref="TenantIsolationManifest"/> (a new endpoint fails CI until
///     someone decides how it is isolated) and every entry must still match a route.
///  2. Tenant A is seeded; tenant B then calls every List route (nothing of A's may appear) and every ById route with A's real ids
///     and the Keyed stores with A's keys, and A's data must be byte-for-byte unchanged afterwards.
/// Routes marked Gap are known holes in this sweep, not claims of safety.
/// </summary>
[Collection("ApiIntegration")]
public sealed class TenantIsolationMatrixTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    internal const string Secret = "ZZ-A-SECRET";

    internal static readonly Guid TenantA = Guid.Parse("a0a0a0a0-0000-4000-8000-00000000000a");
    internal static readonly Guid TenantB = Guid.Parse("b0b0b0b0-0000-4000-8000-00000000000b");

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Normalize(string method, string? pattern)
    {
        var p = pattern ?? string.Empty;
        if (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return $"{method} {p}";
    }

    private HashSet<string> ActualRoutes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(m => Normalize(m, e.RoutePattern.RawText)))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_route_has_an_isolation_entry_and_no_entry_is_stale()
    {
        var actual = ActualRoutes();
        var declared = TenantIsolationManifest.Routes.Keys.ToHashSet(StringComparer.Ordinal);

        actual.Except(declared).Should().BeEmpty(
            "a new endpoint must be added to TenantIsolationManifest as List, ById, Keyed, Global or (explicitly) Gap");
        declared.Except(actual).Should().BeEmpty("manifest entries for routes that no longer exist must be removed");
    }

    [Fact]
    public void No_route_is_left_as_a_Gap()
    {
        TenantIsolationManifest.Routes.Where(kv => kv.Value.Kind == RouteKind.Gap).Select(kv => kv.Key).Should().BeEmpty(
            "every tenant-scoped route is now probed (List/ById/Keyed/Written) or explicitly Global; do not reintroduce holes");
    }

    internal sealed record Seeded(Guid Account, Guid Transaction, Guid Category, Guid RecurringFlow, Guid Asset, Guid SheetBill, Guid Report, Guid Goal, Guid Transaction2, Guid Integration)
    {
        public Guid For(string resource) => resource switch
        {
            "Account" => Account,
            "Transaction" => Transaction,
            "Category" => Category,
            "RecurringFlow" => RecurringFlow,
            "Asset" => Asset,
            "SheetBill" => SheetBill,
            "Report" => Report,
            "Goal" => Goal,
            "Transaction2" => Transaction2,
            "Integration" => Integration,
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
        };

        public IEnumerable<Guid> All => [Account, Transaction, Category, RecurringFlow, Asset, SheetBill, Report, Goal, Transaction2, Integration];
    }

    private static async Task<Guid> CreateAsync(HttpClient c, string url, object body, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        var r = await c.SendAsync(request);
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue($"seeding {url} failed: {r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("id") is { ValueKind: JsonValueKind.String } id && Guid.TryParse(id.GetString(), out var g)
            ? g
            : throw new InvalidOperationException($"No id in response from {url}: {text}");
    }

    internal static async Task<Seeded> SeedTenantAAsync(HttpClient a)
    {
        var account = await CreateAsync(a, "/api/accounts", new CreateAccountRequest($"{Secret} Checking", "Checking", 1234m, "USD", null, null));
        var transaction = await CreateAsync(a, "/api/transactions",
            new CreateTransactionRequest(account, 50m, "USD", "Debit", $"{Secret} Coffee", new DateOnly(2026, 1, 15), null, null, null, null));
        var category = await CreateAsync(a, "/api/categories", new CreateCategoryRequest("zz-a-secret", $"{Secret} Category", null));
        var flow = await CreateAsync(a, "/api/recurring-flows",
            new CreateRecurringFlowRequest(account, $"{Secret} Rent", 1200m, "USD", "Debit", "Fixed", "Monthly", 1, null, null, null,
                new DateOnly(2026, 1, 1), null, null, $"{Secret} Landlord"));
        var asset = await CreateAsync(a, "/api/assets",
            new CreateAssetRequest($"{Secret} Laptop", "Electronics", 2000m, "USD", null, new DateOnly(2024, 1, 1), 2400m, "Apple", "MBP", null, null,
                "Manual", null, null, null, null, null));
        var bill = await CreateAsync(a, "/api/sheet/bills",
            new SaveBillRequest(Secret, $"{Secret} Bill", null, false, false, false, false, false, 4, 100m, null, null, null, null));
        var report = await CreateAsync(a, "/api/reports",
            new SaveReportRequest($"{Secret} Report", "d", "View",
                [new ReportPanel("p", "transactions | summarize t = sum(amount) by merchant", new VizSpec("column", "merchant", ["t"]))]));
        var goal = await CreateAsync(a, "/api/goals", new { name = $"{Secret} Goal", targetAmount = 1000m, savedAmount = 10m, targetDate = (string?)null }, HttpMethod.Put);

        var transaction2 = await CreateAsync(a, "/api/transactions",
            new CreateTransactionRequest(account, 50m, "USD", "Debit", $"{Secret} Coffee", new DateOnly(2026, 1, 15), null, null, null, null));
        var integrationResponse = await a.PostAsJsonAsync("/api/integrations", new
        {
            name = $"{Secret} Hook",
            kind = "Rest",
            enabled = true,
            url = "https://nonexistent.invalid/v1",
            auth = "None",
            authUser = (string?)null,
            credential = (string?)null,
            clearCredential = false,
            headers = (object?)null,
            events = (string[]?)null,
            soapEndpoint = (string?)null,
        });
        integrationResponse.IsSuccessStatusCode.Should().BeTrue("seeding the integration failed");
        var integration = JsonDocument.Parse(await integrationResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("integration").GetProperty("id").GetGuid();

        (await a.PutAsJsonAsync("/api/budgets", new { category = $"{Secret}-budget", monthlyLimit = 111m })).IsSuccessStatusCode.Should().BeTrue();
        (await a.PutAsJsonAsync("/api/category-rules", new { pattern = $"{Secret}-rule", categoryPath = "zz-a-secret" })).IsSuccessStatusCode.Should().BeTrue();
        (await a.PutAsJsonAsync("/api/merchant-aliases", new { pattern = $"{Secret}-alias", alias = $"{Secret} Alias" })).IsSuccessStatusCode.Should().BeTrue();

        return new Seeded(account, transaction, category, flow, asset, bill, report, goal, transaction2, integration);
    }

    /// <summary>Everything tenant A can read, as text: lists plus every seeded row by id. Used before/after B's attacks.</summary>
    internal static async Task<string> SnapshotAsync(HttpClient a, Seeded s)
    {
        var urls = TenantIsolationManifest.Routes
            .Where(kv => kv.Value.Kind == RouteKind.List && kv.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Select(kv => kv.Key[4..])
            .Where(u => !u.Contains("/export") && !u.Contains("/forecast"))
            .Concat([
                $"/api/accounts/{s.Account}", $"/api/transactions/{s.Transaction}", $"/api/categories/{s.Category}",
                $"/api/recurring-flows/{s.RecurringFlow}", $"/api/assets/{s.Asset}", $"/api/reports/{s.Report}",
                $"/api/integrations/{s.Integration}/deliveries"])
            .Order(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var url in urls)
        {
            var r = await a.GetAsync(url);
            // Generated-at style timestamps differ between any two calls; masking them keeps real value changes visible.
            var text = System.Text.RegularExpressions.Regex.Replace(
                await r.Content.ReadAsStringAsync(), @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?", "<ts>");
            sb.Append(url).Append(' ').Append((int)r.StatusCode).Append('\n').Append(text).Append('\n');
        }

        return sb.ToString();
    }

    private static string Resolve(string pattern, Seeded s, string resource)
    {
        var id = s.For(resource).ToString();
        return System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(?<name>[^}:]+)(:[^}]+)?\}", m =>
            m.Groups["name"].Value == "month" ? "2026-01" : id);
    }

    [Fact]
    public async Task Tenant_B_sees_none_of_tenant_A_and_cannot_change_it()
    {
        var a = factory.CreateAuthenticatedClient(TenantA);
        var b = factory.CreateAuthenticatedClient(TenantB);
        var seeded = await SeedTenantAAsync(a);
        var sentinels = new[] { Secret }.Concat(seeded.All.Select(g => g.ToString())).ToArray();
        var before = await SnapshotAsync(a, seeded);
        before.Should().Contain(Secret, "the snapshot must actually contain tenant A's data for the comparison to mean anything");

        var problems = new List<string>();
        var controlHits = 0;

        // ── List routes: B must not see A's data; A (control) must, otherwise the check proves nothing. ──
        foreach (var url in TenantIsolationManifest.Routes.Where(kv => kv.Value.Kind == RouteKind.List && kv.Key.StartsWith("GET ", StringComparison.Ordinal)).Select(kv => kv.Key[4..]))
        {
            var asB = await b.GetAsync(url);
            var bodyB = await asB.Content.ReadAsStringAsync();
            if ((int)asB.StatusCode >= 500)
            {
                problems.Add($"GET {url} as B: server error {(int)asB.StatusCode}");
            }

            foreach (var s in sentinels.Where(bodyB.Contains))
            {
                problems.Add($"GET {url} as B leaked '{s}'");
            }

            var bodyA = await (await a.GetAsync(url)).Content.ReadAsStringAsync();
            if (sentinels.Any(bodyA.Contains))
            {
                controlHits++;
            }
        }

        // ── ById routes: B uses A's real ids. Never a server error, never A's data back, and (checked below) nothing changed. ──
        foreach (var (key, entry) in TenantIsolationManifest.Routes.Where(kv => kv.Value.Kind == RouteKind.ById))
        {
            var space = key.IndexOf(' ');
            var method = new HttpMethod(key[..space]);
            var url = Resolve(key[(space + 1)..], seeded, entry.Resource!);
            var request = new HttpRequestMessage(method, url);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            var response = await b.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode >= 500)
            {
                problems.Add($"{method} {url} as B: server error {(int)response.StatusCode}");
            }

            if (method == HttpMethod.Get && response.IsSuccessStatusCode)
            {
                problems.Add($"GET {url} as B returned {(int)response.StatusCode} for tenant A's row");
            }

            // Only A's data (names carry the sentinel) counts as a leak here: a 404 body may legitimately echo the id B supplied.
            if (body.Contains(Secret))
            {
                problems.Add($"{method} {url} as B leaked '{Secret}'");
            }
        }

        // ── Keyed stores (raw SQL, no EF filter): same key as A, B tries to read, overwrite and delete. ──
        await b.PutAsJsonAsync("/api/budgets", new { category = $"{Secret}-budget", monthlyLimit = 1m });
        await b.DeleteAsync($"/api/budgets?category={Uri.EscapeDataString($"{Secret}-budget")}");
        await b.PutAsJsonAsync("/api/category-rules", new { pattern = $"{Secret}-rule", categoryPath = "other" });
        await b.DeleteAsync($"/api/category-rules?pattern={Uri.EscapeDataString($"{Secret}-rule")}");
        await b.PutAsJsonAsync("/api/merchant-aliases", new { pattern = $"{Secret}-alias", alias = "HIJACKED" });
        await b.DeleteAsync($"/api/merchant-aliases?pattern={Uri.EscapeDataString($"{Secret}-alias")}");
        await b.PutAsJsonAsync("/api/goals", new { id = seeded.Goal, name = "HIJACKED", targetAmount = 5m, savedAmount = 1m, targetDate = (string?)null });

        var after = await SnapshotAsync(a, seeded);

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
        controlHits.Should().BeGreaterThanOrEqualTo(8, "tenant A must see its own seeded data on most list routes, otherwise this sweep cannot detect a leak");
        after.Should().Be(before, "nothing tenant B did may change tenant A's data");
        after.Should().NotContain("HIJACKED");
    }
}
