using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class TransactionsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Handler : HttpMessageHandler
    {
        public int TransactionCalls;
        public readonly List<string> Queries = [];
        /// <summary>When set, the fake server holds this many rows, pages them by skip/take and reports X-Total-Count.</summary>
        public int? ServerTotal;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path == "/api/transactions")
            {
                Interlocked.Increment(ref TransactionCalls);
                Queries.Add(request.RequestUri.Query);
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var skip = int.TryParse(query["skip"], out var sk) ? sk : 0;
                var take = int.TryParse(query["take"], out var tk) ? tk : 100;
                var rows = ServerTotal is { } total ? Math.Max(0, Math.Min(take, total - skip)) : 0;
                json = rows == 0 ? "[]" : "[" + string.Join(",", Enumerable.Range(0, rows).Select(_ => TxJson())) + "]";
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (ServerTotal is { } t)
                {
                    ok.Headers.Add("X-Total-Count", t.ToString());
                }

                return Task.FromResult(ok);
            }
            else if (path == "/api/accounts")
            {
                json = $$"""[{"id":"{{AccountId}}","name":"Checking","accountType":"Checking","balanceAmount":1,"currency":"USD","isActive":true}]""";
            }
            else
            {
                json = "[]";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static string TxJson() =>
        $$"""{"id":"{{Guid.NewGuid()}}","accountId":"{{AccountId}}","amount":5,"currency":"USD","direction":"Debit","description":"ROW","status":"Posted","source":"x","effectiveDate":"2026-01-01","createdAt":"1970-01-01T00:00:00Z"}""";

    private IRenderedComponent<Transactions> RenderPage(Handler handler)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<Transactions>();
    }

    [Fact]
    public async Task Search_is_sent_to_the_server_instead_of_filtering_loaded_rows()
    {
        var handler = new Handler();
        var cut = RenderPage(handler);
        cut.WaitForState(() => handler.TransactionCalls >= 1);

        var field = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label?.StartsWith("Search") == true);
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("coffee shop"));

        handler.Queries.Last().Should().Contain("search=coffee%20shop");
    }

    [Fact]
    public void The_grid_shows_the_server_total_not_just_what_was_loaded()
    {
        var handler = new Handler { ServerTotal = 1234 };
        var cut = RenderPage(handler);

        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Be("1–50 of 1,234"));
    }

    [Fact]
    public async Task Next_page_asks_the_server_for_the_next_slice_and_previous_goes_back()
    {
        var handler = new Handler { ServerTotal = 120 };
        var cut = RenderPage(handler);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Be("1–50 of 120"));

        await cut.Find("button[aria-label='Next page']").ClickAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Be("51–100 of 120"));
        handler.Queries.Last().Should().Contain("skip=50");

        await cut.Find("button[aria-label='Next page']").ClickAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Be("101–120 of 120"));
        cut.Find("button[aria-label='Next page']").HasAttribute("disabled").Should().BeTrue();

        await cut.Find("button[aria-label='Previous page']").ClickAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Be("51–100 of 120"));
    }

    [Fact]
    public void A_big_result_never_renders_more_rows_than_the_page_size()
    {
        var handler = new Handler { ServerTotal = 10_000 };
        var cut = RenderPage(handler);

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().BeInRange(1, 100));
    }

    [Fact]
    public void Previous_is_disabled_on_the_first_page_and_changing_page_size_goes_back_to_the_start()
    {
        var handler = new Handler { ServerTotal = 120 };
        var cut = RenderPage(handler);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tx-range]").TextContent.Should().Contain("of 120"));

        cut.Find("button[aria-label='Previous page']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task The_row_details_panel_opens_with_the_full_transaction_and_its_actions()
    {
        var handler = new Handler { ServerTotal = 1 };
        var cut = RenderPage(handler);
        var provider = Render<MudDialogProvider>();
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label^='Show details for']").Should().NotBeEmpty());

        // The click handler awaits the dialog's result, so it only completes once the panel closes: do not await it here.
        var opened = cut.Find("button[aria-label^='Show details for']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Transaction details").And.Contain("Rename merchant").And.Contain("Split");
            provider.Markup.Should().Contain("ROW");
        });

        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Close").Click();
        await opened;
    }

    private static TransactionDto Tx(string merchant, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), AccountId, 5m, "USD", "Debit", merchant.ToUpperInvariant() + " #1", "Posted", "x",
            new DateOnly(2026, 1, 1), null, null, merchant, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Merchant_match_counts_other_rows_from_the_same_merchant_case_insensitively()
    {
        var target = Tx("Netflix");
        var rows = new[] { target, Tx("netflix"), Tx("Netflix"), Tx("Spotify") };

        MerchantMatch.CountOthers(rows, target).Should().Be(2);
    }

    [Fact]
    public void Merchant_match_falls_back_to_the_description_when_there_is_no_merchant_name()
    {
        var a = Tx("") with { MerchantName = null, Description = "ACME 42" };
        var b = Tx("") with { MerchantName = null, Description = "acme 42" };

        MerchantMatch.CountOthers([a, b], a).Should().Be(1);
    }

    [Fact]
    public async Task Changing_the_account_filter_reloads_once_without_pressing_a_button()
    {
        var handler = new Handler();
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<Transactions>();
        cut.WaitForState(() => handler.TransactionCalls >= 1);
        var before = handler.TransactionCalls;

        var select = cut.FindComponent<MudSelect<Guid?>>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(AccountId));

        handler.TransactionCalls.Should().Be(before + 1);
    }

    [Fact]
    public void Bulk_apply_to_same_merchant_is_off_by_default()
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<Transactions>();

        var box = cut.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label?.Contains("same merchant") == true);
        box.Find("input[type=checkbox]").HasAttribute("checked").Should().BeFalse();
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
