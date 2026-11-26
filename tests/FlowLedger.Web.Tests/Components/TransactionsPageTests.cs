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
        public int PageRows;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path == "/api/transactions")
            {
                Interlocked.Increment(ref TransactionCalls);
                Queries.Add(request.RequestUri.Query);
                json = PageRows == 0 ? "[]" : "[" + string.Join(",", Enumerable.Range(0, PageRows).Select(_ => TxJson())) + "]";
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
    public async Task A_full_page_offers_older_transactions_and_loading_them_skips_what_is_loaded()
    {
        var handler = new Handler { PageRows = 500 };
        var cut = RenderPage(handler);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Load older transactions"));

        await cut.Find("button[aria-label='Load older transactions']").ClickAsync();

        handler.Queries.Last().Should().Contain("skip=500");
    }

    [Fact]
    public void A_short_page_has_no_load_older_button()
    {
        var handler = new Handler { PageRows = 3 };
        var cut = RenderPage(handler);
        cut.WaitForState(() => handler.TransactionCalls >= 1);

        cut.Markup.Should().NotContain("Load older transactions");
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
