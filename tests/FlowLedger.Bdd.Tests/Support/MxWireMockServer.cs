using System.Globalization;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Bdd.Tests.Support;

/// <summary>
/// Boots a WireMock.Net server stubbing the MX Platform API with a deterministic dataset.
/// Recreated in-project (mirrors FlowLedger.Integrations.Tests/Mx/MxWireMockFixture) so the BDD
/// project does not cross-reference another test assembly.
///
/// The MX provider is wired via public DI (<c>AddMxProvider</c>) with <see cref="BaseUrl"/> pointing
/// here, so this fixture exposes only the wire stubs — no internal MX types are referenced.
///
/// Dataset:
///   user   USR-test, member MBR-test (CONNECTED)
///   accounts: ACT-checking (30 txns), ACT-savings (3 txns) → 33 transactions total
/// With records_per_page = 25, ACT-checking spans 2 pages (25 + 5).
/// </summary>
public sealed class MxWireMockServer : IDisposable
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

    public MxWireMockServer()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });
        ConfigureStubs();
    }

    /// <summary>Base URL the MX provider points at.</summary>
    public string BaseUrl => _server.Url!;

    public int RequestCount(string pathFragment) =>
        _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains(pathFragment, StringComparison.Ordinal) == true);

    // ── Stub configuration ──────────────────────────────────────────────────────

    private void ConfigureStubs()
    {
        _server
            .Given(Request.Create().WithPath("/users").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(UserJson()));

        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/members").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(MemberJson("CONNECTED")));

        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/widget_urls").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(WidgetJson()));

        _server
            .Given(Request.Create().WithPath($"/users/{UserGuid}/members/{MemberGuid}/status").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(MemberJson("CONNECTED")));

        _server
            .Given(Request.Create()
                .WithPath($"/users/{UserGuid}/members/{MemberGuid}/accounts")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(AccountsJson()));

        ConfigureTransactionStubs(CheckingAccountGuid, CheckingTransactionCount);
        ConfigureTransactionStubs(SavingsAccountGuid, SavingsTransactionCount);
    }

    private void ConfigureTransactionStubs(string accountGuid, int totalCount)
    {
        var totalPages = (int)Math.Ceiling(totalCount / (double)RecordsPerPage);
        var basePath = $"/users/{UserGuid}/accounts/{accountGuid}/transactions";

        // Fallback (lowest priority): any page beyond the data returns an EMPTY page reporting
        // the true total_pages, so HasMore is false. This mirrors a real provider that resumes
        // from a persisted cursor pointing past the last page (the second incremental sync) and
        // returns nothing new instead of 404. Specific per-page stubs below win via higher priority.
        _server
            .Given(Request.Create().WithPath(basePath).UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(EmptyTransactionsJson(totalCount, totalPages)));

        for (var page = 1; page <= Math.Max(1, totalPages); page++)
        {
            var p = page;
            _server
                .Given(Request.Create()
                    .WithPath(basePath)
                    .WithParam("page", p.ToString(CultureInfo.InvariantCulture))
                    .UsingGet())
                .AtPriority(1)
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", Vnd)
                    .WithBody(TransactionsJson(accountGuid, p, totalCount, totalPages)));
        }
    }

    private static string EmptyTransactionsJson(int totalCount, int totalPages)
    {
        // current_page set beyond total_pages so HasMore (current < total) is false.
        var pagination = Obj(
            $"\"current_page\":{totalPages + 1},\"per_page\":{RecordsPerPage}," +
            $"\"total_entries\":{totalCount},\"total_pages\":{totalPages}");
        return Obj($"\"transactions\":{Arr([])},\"pagination\":{pagination}");
    }

    // ── JSON builders (deterministic) ─────────────────────────────────────────────

    private static string Obj(string innerFields) => "{" + innerFields + "}";

    private static string Arr(IEnumerable<string> elements) => "[" + string.Join(",", elements) + "]";

    private static string UserJson() =>
        Obj($"\"user\":{Obj($"\"guid\":\"{UserGuid}\",\"id\":\"flowledger-bdd\"")}");

    private static string WidgetJson()
    {
        var widget = Obj(
            "\"type\":\"connect_widget\"," +
            "\"url\":\"https://int-widgets.moneydesktop.com/md/connect/abc123\"," +
            "\"user_id\":\"flowledger-bdd\"");
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
