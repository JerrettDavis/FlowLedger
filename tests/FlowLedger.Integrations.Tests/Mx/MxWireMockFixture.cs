using System.Globalization;
using System.Text;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Mx.CostControl;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Boots a WireMock.Net server stubbing the MX Platform API with a deterministic dataset,
/// and constructs a real <see cref="MxFinancialDataProvider"/> wired to it with fake creds.
///
/// Dataset (deterministic, byte-stable):
///   user   USR-test
///   member MBR-test (connection_status CONNECTED)
///   accounts: ACT-checking (30 txns), ACT-savings (3 txns)
/// With records_per_page = 25, ACT-checking spans 2 pages (25 + 5) so pagination/cursor cases
/// exercise more than one page.
/// </summary>
public sealed class MxWireMockFixture : IDisposable
{
    public const string UserGuid = "USR-test";
    public const string MemberGuid = "MBR-test";
    public const string CheckingAccountGuid = "ACT-checking";
    public const string SavingsAccountGuid = "ACT-savings";
    public const string InstitutionName = "WireMock Bank";
    public const int CheckingTransactionCount = 30;
    public const int SavingsTransactionCount = 3;
    public const int RecordsPerPage = 25;

    private const string Vnd = "application/vnd.mx.api.v1+json";

    private readonly WireMockServer _server;

    public MxWireMockFixture()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });
        ConfigureStubs();
    }

    /// <summary>Base URL the MxApiClient points at.</summary>
    public string BaseUrl => _server.Url!;

    /// <summary>Request log for asserting which MX endpoints were hit.</summary>
    public IEnumerable<WireMock.Logging.ILogEntry> RequestLog => _server.LogEntries;

    public int RequestCount(string pathFragment) =>
        _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains(pathFragment, StringComparison.Ordinal) == true);

    public void Reset() => _server.ResetLogEntries();

    /// <summary>Builds a fully-wired MX provider against the WireMock server with fake creds.</summary>
    public MxFinancialDataProvider CreateProvider(int recordsPerPage = RecordsPerPage)
    {
        var client = CreateApiClient();
        var verifier = new MxWebhookVerifier("wiremock-test-secret");
        var options = Options.Create(new MxProviderOptions
        {
            RecordsPerPage = recordsPerPage,
            DefaultInstitutionCode = "wiremock",
            ManualRefreshCooldown = TimeSpan.FromMinutes(15),
        });

        return new MxFinancialDataProvider(
            client, verifier, options, NullLogger<MxFinancialDataProvider>.Instance);
    }

    /// <summary>Builds just the typed API client against the WireMock server.</summary>
    internal MxApiClient CreateApiClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl, UriKind.Absolute) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-client:test-key"));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(Vnd));

        return new MxApiClient(http, NullLogger<MxApiClient>.Instance);
    }

    /// <summary>Convenience: a fresh in-memory-backed cooldown gate for cost-control tests.</summary>
    public static MxRefreshCooldown CreateCooldown(TimeSpan window, TimeProvider clock)
    {
        IDistributedCache cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var options = Options.Create(new MxProviderOptions { ManualRefreshCooldown = window });
        return new MxRefreshCooldown(cache, options, clock);
    }

    // ── Stub configuration ──────────────────────────────────────────────────────

    private void ConfigureStubs()
    {
        // POST /users → create user
        _server
            .Given(Request.Create().WithPath("/users").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(UserJson()));

        // POST /users/{user}/members → create member
        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/members").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(MemberJson("CONNECTED")));

        // POST /users/{user}/widget_urls → connect widget
        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/widget_urls").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(WidgetJson()));

        // GET /users/{user}/members/{member}/status → CONNECTED
        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/members/{MemberGuid}/status").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(MemberJson("CONNECTED")));

        // GET accounts (single page — 2 accounts)
        _server
            .Given(Request.Create()
                .WithPath($"/users/{UserGuid}/members/{MemberGuid}/accounts")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(AccountsJson()));

        // GET transactions — paged. Two accounts, page-aware bodies.
        ConfigureTransactionStubs(CheckingAccountGuid, CheckingTransactionCount);
        ConfigureTransactionStubs(SavingsAccountGuid, SavingsTransactionCount);
    }

    private void ConfigureTransactionStubs(string accountGuid, int totalCount)
    {
        var totalPages = (int)Math.Ceiling(totalCount / (double)RecordsPerPage);
        var basePath = $"/users/{UserGuid}/accounts/{accountGuid}/transactions";

        for (var page = 1; page <= Math.Max(1, totalPages); page++)
        {
            var p = page;
            _server
                .Given(Request.Create()
                    .WithPath(basePath)
                    .WithParam("page", p.ToString(CultureInfo.InvariantCulture))
                    .UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", Vnd)
                    .WithBody(TransactionsJson(accountGuid, p, totalCount, totalPages)));
        }
    }

    // ── JSON builders (deterministic) ─────────────────────────────────────────────
    //
    // Built with the Obj/Arr helpers (plain string concatenation) rather than raw string
    // literals, to avoid brace-collision between JSON's '}' and C# raw-interpolation tokens.

    private static string Obj(string innerFields) => "{" + innerFields + "}";

    private static string Arr(IEnumerable<string> elements) => "[" + string.Join(",", elements) + "]";

    private static string UserJson() =>
        Obj($"\"user\":{Obj($"\"guid\":\"{UserGuid}\",\"id\":\"flowledger-test\"")}");

    private static string WidgetJson()
    {
        var widget = Obj(
            "\"type\":\"connect_widget\"," +
            "\"url\":\"https://int-widgets.moneydesktop.com/md/connect/abc123\"," +
            "\"user_id\":\"flowledger-test\"");
        return Obj($"\"widget_url\":{widget}");
    }

    private static string MemberJson(string status)
    {
        var member = Obj(
            $"\"guid\":\"{MemberGuid}\"," +
            $"\"name\":\"{InstitutionName}\"," +
            "\"institution_code\":\"wiremock\"," +
            $"\"connection_status\":\"{status}\"," +
            "\"connection_status_message\":\"OK\"," +
            "\"is_being_aggregated\":false," +
            $"\"user_guid\":\"{UserGuid}\"");
        return Obj($"\"member\":{member}");
    }

    private static string AccountsJson()
    {
        var checking = Obj(
            $"\"guid\":\"{CheckingAccountGuid}\",\"name\":\"WireMock Checking\",\"type\":\"CHECKING\"," +
            "\"subtype\":\"NONE\",\"balance\":1234.56,\"available_balance\":1200.00,\"currency_code\":\"USD\"," +
            $"\"member_guid\":\"{MemberGuid}\",\"user_guid\":\"{UserGuid}\"");
        var savings = Obj(
            $"\"guid\":\"{SavingsAccountGuid}\",\"name\":\"WireMock Savings\",\"type\":\"SAVINGS\"," +
            "\"subtype\":\"NONE\",\"balance\":9000.00,\"available_balance\":9000.00,\"currency_code\":\"USD\"," +
            $"\"member_guid\":\"{MemberGuid}\",\"user_guid\":\"{UserGuid}\"");
        var pagination = Obj("\"current_page\":1,\"per_page\":25,\"total_entries\":2,\"total_pages\":1");
        return Obj($"\"accounts\":{Arr([checking, savings])},\"pagination\":{pagination}");
    }

    private static string TransactionsJson(string accountGuid, int page, int totalCount, int totalPages)
    {
        var startIndex = (page - 1) * RecordsPerPage;
        var endExclusive = Math.Min(startIndex + RecordsPerPage, totalCount);

        var items = new List<string>();
        for (var i = startIndex; i < endExclusive; i++)
        {
            // Deterministic per-index values. Alternate DEBIT/CREDIT, stable dates/descriptions.
            var type = i % 2 == 0 ? "DEBIT" : "CREDIT";
            var amount = (10.00m + i).ToString(CultureInfo.InvariantCulture);
            var day = (i % 28) + 1;
            var date = $"2026-01-{day:D2}";
            var status = i % 5 == 0 ? "PENDING" : "POSTED";

            items.Add(Obj(
                $"\"guid\":\"TRN-{accountGuid}-{i}\",\"account_guid\":\"{accountGuid}\"," +
                $"\"amount\":{amount},\"currency_code\":\"USD\",\"date\":\"{date}\"," +
                $"\"posted_at\":\"{date}T12:00:00Z\",\"transacted_at\":\"{date}T12:00:00Z\"," +
                $"\"description\":\"Merchant {i}\",\"original_description\":\"MERCHANT {i} RAW\"," +
                $"\"category\":\"Shopping\",\"top_level_category\":\"Expenses\"," +
                $"\"status\":\"{status}\",\"type\":\"{type}\""));
        }

        var pagination = Obj(
            $"\"current_page\":{page},\"per_page\":{RecordsPerPage}," +
            $"\"total_entries\":{totalCount},\"total_pages\":{totalPages}");
        return Obj($"\"transactions\":{Arr(items)},\"pagination\":{pagination}");
    }

    public void Dispose() => _server.Dispose();
}
