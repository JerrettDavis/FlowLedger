using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>The Data page shows the last restore drill in the app: live vs restored row counts and the raw-archive check (#62).</summary>
public sealed class DataPageDrillTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static string Status(string drill, bool drillOk = true) => $$$"""
        {"environment":"production",
         "backup":{"healthy":true,"stale":false,"offsiteConfigured":false,"lastSuccessUtc":"2026-11-27T03:00:00Z",
           "lastRun":null,
           "runs":[{"ok":true,"startedUtc":"2026-11-27T03:00:00Z","finishedUtc":"2026-11-27T03:01:00Z","snapshotDir":"snap","rows":120,
                    "offsiteOk":null,"offsiteDetail":null,"drillOk":{{{(drillOk ? "true" : "false")}}},"error":null,"drill":{{{drill}}}}]},
         "providerUsage":{"provider":"MX","callsToday":0,"callsThisMonth":0,"dailyBudget":500,"monthlyBudget":5000,"paused":false,"byEndpoint":[]}}
        """;

    private IRenderedComponent<DataPage> RenderPage(string json)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler(json)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        Services.AddSingleton(new FlowLedger.Web.Auth.OidcOptions());
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        return Render<DataPage>();
    }

    [Fact]
    public void Shows_the_last_drill_with_live_versus_restored_counts_and_the_raw_archive_check()
    {
        var cut = RenderPage(Status("""
            {"tables":[
               {"table":"accounts","expectedRows":2,"restoredRows":2,"liveRows":2,"ok":true},
               {"table":"transactions","expectedRows":100,"restoredRows":100,"liveRows":130,"ok":true}],
             "rawArchiveChecked":true,"rawObjectsVerified":42,"rawObjectsSealed":3,"problems":[]}
            """));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=restore-drill-result]").Should().HaveCount(1));
        cut.Find("[data-testid=restore-drill-result]").TextContent.Should().Contain("Passed").And.Contain("2 of 2 tables restored");
        cut.Find("[data-testid=restore-drill-live]").TextContent.Should().Contain("1 table has a different live row count");
        cut.Find("[data-testid=restore-drill-raw]").TextContent.Should().Contain("42 objects hash-verified").And.Contain("3 encrypted objects counted only");

        var rows = cut.FindAll("[data-testid=restore-drill-tables] tbody tr").Select(r => r.TextContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        rows.Should().HaveCount(2);
        var transactions = cut.FindAll("[data-testid=restore-drill-tables] tbody tr").Single(r => r.TextContent.Contains("transactions", StringComparison.Ordinal));
        transactions.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).Should().Equal("transactions", "100", "100", "130");
    }

    [Fact]
    public void A_failed_drill_lists_the_mismatch_and_a_missing_raw_archive_is_said_plainly()
    {
        var cut = RenderPage(Status("""
            {"tables":[{"table":"accounts","expectedRows":2,"restoredRows":1,"liveRows":2,"ok":false}],
             "rawArchiveChecked":false,"rawObjectsVerified":0,"rawObjectsSealed":0,"problems":["accounts: expected 2 rows, restored 1"]}
            """, drillOk: false));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=restore-drill-result]").Should().HaveCount(1));
        cut.Find("[data-testid=restore-drill-result]").TextContent.Should().Contain("FAILED").And.Contain("0 of 1 tables restored");
        cut.Markup.Should().Contain("accounts: expected 2 rows, restored 1");
        cut.Find("[data-testid=restore-drill-raw]").TextContent.Should().Contain("not checked");
    }

    [Fact]
    public void Without_any_recorded_drill_the_page_says_so()
    {
        var cut = RenderPage(Status("null"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=restore-drill-none]").Should().HaveCount(1));
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
