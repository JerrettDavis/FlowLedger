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

/// <summary>Budget rollover (#70): the page shows what carried over, and saving sends the rollover choice.</summary>
public sealed class BudgetsPageTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Saved = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/budgets")
            {
                Saved.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var json = request.RequestUri!.AbsolutePath == "/api/budgets"
                ? """
                  {"month":"2026-11","spendByCategory":{"Food":130},"budgets":[
                    {"category":"Food","monthlyLimit":100,"spent":130,"remaining":30,"percentUsed":81.2,"over":false,"version":7,"rollover":true,"carried":60},
                    {"category":"Fun","monthlyLimit":50,"spent":10,"remaining":40,"percentUsed":20,"over":false,"version":8,"rollover":false,"carried":0}]}
                  """
                : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private (IRenderedComponent<BudgetsPage> Cut, Handler Handler) RenderPage()
    {
        var handler = new Handler();
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return (Render<BudgetsPage>(), handler);
    }

    [Fact]
    public void A_rollover_budget_shows_the_carried_amount_and_judges_spending_against_the_larger_limit()
    {
        var (cut, _) = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=budget-carried]").Should().HaveCount(1, "only the Food budget rolls over"));
        cut.Find("[data-testid=budget-carried]").TextContent.Should().Contain("$60").And.Contain("$100");
        cut.Markup.Should().Contain("$160", "130 spent of the 100 limit plus the 60 carried");
    }

    [Fact]
    public async Task Saving_sends_the_rollover_choice_of_the_picked_budget()
    {
        var (cut, handler) = RenderPage();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=budget-carried]").Should().HaveCount(1));

        await cut.Find("button[aria-label='Edit budget for Food']").ClickAsync(new());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Save budget", StringComparison.Ordinal)).ClickAsync(new());

        handler.Saved.Should().ContainSingle().Which.Should().Contain("\"rollover\":true").And.Contain("\"category\":\"Food\"").And.Contain("\"monthlyLimit\":100");
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
