using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class QueryEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<AccountDto> Account(HttpClient client, string name)
        => (await (await client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest(name, "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>())!;

    private static Task<HttpResponseMessage> Spend(HttpClient client, Guid account, decimal amount, string desc)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return client.PostAsJsonAsync("/api/transactions",
            new CreateTransactionRequest(account, amount, "USD", "Debit", desc, today, today, null, desc, null));
    }

    [Fact]
    public async Task Ask_with_a_blank_question_is_answered_without_calling_any_model()
    {
        var response = await _client.PostAsJsonAsync("/api/query/ask", new AskQueryRequest("   "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AskQueryResult>();
        body!.Query.Should().BeNull();
        body.Error.Should().NotBeNullOrWhiteSpace();
        body.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Run_returns_aggregated_rows_for_the_callers_data_only()
    {
        var mine = await Account(_client, "Mine");
        await Spend(_client, mine.Id, 12m, "Coffee");
        await Spend(_client, mine.Id, 8m, "Coffee");

        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var theirs = await Account(other, "Theirs");
        await Spend(other, theirs.Id, 999m, "Yacht");

        var response = await _client.PostAsJsonAsync("/api/query/run",
            new RunQueryRequest("transactions | summarize total = sum(amount), n = count() by merchant"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<QueryResult>())!;

        result.Rows.Should().ContainSingle();
        result.Columns.Select(c => c.Name).Should().Equal("merchant", "total", "n");
    }

    [Fact]
    public async Task Run_reports_syntax_errors_as_400_with_position()
    {
        var response = await _client.PostAsJsonAsync("/api/query/run", new RunQueryRequest("transactions | where nope == 1"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Unknown column");
    }

    [Fact]
    public async Task Schema_and_presets_are_available()
    {
        var schema = (await _client.GetFromJsonAsync<QuerySchemaDto>("/api/query/schema"))!;
        schema.Datasets.Select(d => d.Name).Should().Contain(["transactions", "accounts", "assets"]);

        var reports = (await _client.GetFromJsonAsync<List<SavedReportDto>>("/api/reports"))!;
        reports.Where(r => r.IsPreset).Should().HaveCountGreaterThan(5);

        var run = await _client.GetAsync("/api/reports/preset:monthly-overview/run");
        run.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await run.Content.ReadFromJsonAsync<RunReportResultDto>())!;
        result.Panels.Should().OnlyContain(p => p.Error == null);
    }

    [Fact]
    public async Task Save_update_run_and_delete_a_view()
    {
        var request = new SaveReportRequest("Coffee", "d", "View",
            [new ReportPanel("Coffee spend", "transactions | summarize t = sum(amount) by merchant", new VizSpec("column", "merchant", ["t"]))]);
        var created = await _client.PostAsJsonAsync("/api/reports", request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var saved = (await created.Content.ReadFromJsonAsync<SavedReportDto>())!;
        saved.IsPreset.Should().BeFalse();

        var updated = await _client.PutAsJsonAsync($"/api/reports/{saved.Id}", request with { Name = "Coffee v2" });
        (await updated.Content.ReadFromJsonAsync<SavedReportDto>())!.Name.Should().Be("Coffee v2");

        (await _client.GetAsync($"/api/reports/{saved.Id}/run")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.DeleteAsync($"/api/reports/{saved.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/reports/{saved.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Save_rejects_unparsable_queries_and_multi_panel_views()
    {
        var bad = new SaveReportRequest("Bad", null, "View", [new ReportPanel("p", "transactions | wherr x", new VizSpec("table"))]);
        (await _client.PostAsJsonAsync("/api/reports", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var panel = new ReportPanel("p", "transactions | take 1", new VizSpec("table"));
        var twoPanelView = new SaveReportRequest("Two", null, "View", [panel, panel]);
        (await _client.PostAsJsonAsync("/api/reports", twoPanelView)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Saved_reports_are_tenant_isolated_and_presets_are_not_deletable()
    {
        var panel = new ReportPanel("p", "transactions | take 1", new VizSpec("table"));
        var saved = (await (await _client.PostAsJsonAsync("/api/reports", new SaveReportRequest("Mine", null, "View", [panel])))
            .Content.ReadFromJsonAsync<SavedReportDto>())!;

        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        (await other.GetAsync($"/api/reports/{saved.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/reports/{saved.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync("/api/reports/preset:pending")).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Query_requires_authentication()
    {
        using var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/query/run", new RunQueryRequest("transactions"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}
