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

/// <summary>The subscriptions page (#70): the detected list, its totals, and the price-change flag.</summary>
public sealed class SubscriptionsPageTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private IRenderedComponent<SubscriptionsPage> RenderPage(string json)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler(json)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<SubscriptionsPage>();
    }

    private const string TwoSubscriptions = """
        {"subscriptions":[
          {"name":"Streamly Video","cadence":"Monthly","status":"Active","amount":17.99,"monthlyCost":17.99,"firstCharged":"2026-08-01","lastCharged":"2026-11-01","nextExpected":"2026-12-01","charges":4,"priceChange":"Increase","previousAmount":15.99,"priceChangedOn":"2026-11-01"},
          {"name":"Annual Cloud Plan","cadence":"Yearly","status":"Active","amount":120,"monthlyCost":10,"firstCharged":"2025-03-01","lastCharged":"2026-03-01","nextExpected":"2027-03-01","charges":2,"priceChange":null,"previousAmount":null,"priceChangedOn":null}],
         "monthlyTotal":27.99,"yearlyTotal":335.88}
        """;

    [Fact]
    public void Lists_each_subscription_with_the_totals_and_flags_the_price_increase()
    {
        var cut = RenderPage(TwoSubscriptions);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=subscription-row]").Should().HaveCount(2));
        cut.Find("[data-testid=subscription-monthly-total]").TextContent.Should().Contain("$27.99");
        cut.FindAll("[data-testid=subscription-price-change]").Should().ContainSingle().Which.TextContent.Should().Contain("Up from $15.99");
        cut.Find("[data-testid=subscription-price-alert]").TextContent.Should().Contain("1 subscription went up");
        cut.Markup.Should().Contain("Steady");
    }

    [Fact]
    public void Shows_an_empty_state_when_nothing_was_detected()
    {
        var cut = RenderPage("""{"subscriptions":[],"monthlyTotal":0,"yearlyTotal":0}""");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No subscriptions found"));
        cut.FindAll("[data-testid=subscription-row]").Should().BeEmpty();
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
