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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path == "/api/transactions")
            {
                Interlocked.Increment(ref TransactionCalls);
                json = "[]";
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
