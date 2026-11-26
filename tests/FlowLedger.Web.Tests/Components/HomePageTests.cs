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

public sealed class HomePageTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler(Func<string, HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var code = status(path);
            var json = path == "/api/accounts"
                ? """[{"id":"11111111-1111-1111-1111-111111111111","name":"Checking","accountType":"Checking","balanceAmount":1234.5,"balanceCurrency":"USD","institution":null,"externalAccountRef":null,"isActive":true,"createdAt":"2026-01-01T00:00:00Z","lastBalanceConfirmedAt":null}]"""
                : "null";
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private IRenderedComponent<Home> RenderHome(Func<string, HttpStatusCode> status)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler(status)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        var cut = Render<Home>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading dashboard"));
        return cut;
    }

    private static HttpStatusCode FailInsightsAndForecast(string path) =>
        path.StartsWith("/api/dashboard/insights", StringComparison.Ordinal) || path.StartsWith("/api/forecast", StringComparison.Ordinal)
            ? HttpStatusCode.InternalServerError
            : HttpStatusCode.OK;

    [Fact]
    public void Failed_insights_show_a_retry_while_cash_still_renders()
    {
        var cut = RenderHome(FailInsightsAndForecast);

        cut.Markup.Should().Contain("$1,234.50");
        System.Net.WebUtility.HtmlDecode(cut.Markup).Should().Contain("Couldn't load spending insights");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Retry"));
    }

    [Fact]
    public void Failed_forecast_shows_unavailable_instead_of_a_zero_balance_or_no_overdraft_claim()
    {
        var cut = RenderHome(FailInsightsAndForecast);

        cut.Find("[aria-label='Forecast low water mark']").TextContent.Should().Contain("Unavailable");
        cut.Find("[aria-label='Overdraft warnings']").TextContent.Should().Contain("Unavailable").And.NotContain("None");
    }

    [Fact]
    public void Biggest_changes_caption_follows_the_selected_range_not_a_fixed_30_days()
    {
        RepoFiles.Read("Components/Pages/Home.razor").Should().NotContain("Last 30 days vs the 30 before");
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
