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

/// <summary>
/// #211: the inline forms (budgets, goals, debt payoff) show the API's per-field validation errors on the field that caused them,
/// keep what the user typed, and give the money inputs the display currency's symbol.
/// </summary>
public sealed class InlineFormFieldErrorTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler(string rejectionErrorsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get && path is "/api/budgets" or "/api/goals" or "/api/debt/plan")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"title\":\"One or more validation errors occurred.\",\"errors\":" + rejectionErrorsJson + "}", Encoding.UTF8, "application/problem+json"),
                });
            }

            var json = path == "/api/budgets"
                ? """{"month":"2026-11","spendByCategory":{"Food":130},"budgets":[{"category":"Food","monthlyLimit":100,"spent":130,"remaining":30,"percentUsed":81.2,"over":false,"version":7,"rollover":false,"carried":0}]}"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private void Arrange(string rejectionErrorsJson)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler(rejectionErrorsJson)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
    }

    [Fact]
    public async Task Budget_limit_error_from_the_api_is_shown_on_the_limit_field_and_the_typed_values_stay()
    {
        // The budget validator keys the limit failure to MonthlyLimit, so the message lands on the limit input itself.
        Arrange("""{"MonthlyLimit":["The monthly limit must be between 0 and 100,000,000."]}""");
        var cut = Render<BudgetsPage>();
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Edit budget for Food']").Should().HaveCount(1));
        await cut.Find("button[aria-label='Edit budget for Food']").ClickAsync(new());

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Save budget", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("The monthly limit must be between 0 and 100,000,000.").And.Contain("Fix the highlighted fields");
            cut.Find("input[aria-label='Monthly limit']").GetAttribute("value").Should().StartWith("100");
            cut.Find("input[aria-label='Monthly limit']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Editing_the_budget_limit_clears_the_server_error()
    {
        Arrange("""{"MonthlyLimit":["The monthly limit must be between 0 and 100,000,000."]}""");
        var cut = Render<BudgetsPage>();
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Edit budget for Food']").Should().HaveCount(1));
        await cut.Find("button[aria-label='Edit budget for Food']").ClickAsync(new());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Save budget", StringComparison.Ordinal)).ClickAsync(new());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("The monthly limit must be between 0 and 100,000,000."));

        cut.Find("input[aria-label='Monthly limit']").Change("120");

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("The monthly limit must be between 0 and 100,000,000."));
    }

    [Fact]
    public async Task Goal_target_error_is_marked_on_the_target_field_and_the_summary_is_an_alert()
    {
        Arrange("""{"TargetAmount":["The target must be greater than 0 and at most 1,000,000,000."]}""");
        var cut = Render<GoalsPage>();
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Save goal")));
        cut.Find("input[type=text]").Input("Trip");
        cut.Find("input[aria-label='Target']").Input("500");

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Save goal", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.Should().Contain("Fix the highlighted fields");
            cut.Markup.Should().Contain("The target must be greater than 0 and at most 1,000,000,000.");
            cut.Find("input[aria-label='Target']").GetAttribute("aria-invalid").Should().Be("true");
            cut.Find("input[aria-label='Saved so far']").GetAttribute("aria-invalid").Should().NotBe("true");
        });
    }

    [Fact]
    public void Goal_and_budget_money_inputs_show_the_display_currency_symbol()
    {
        Arrange("{}");
        var goals = Render<GoalsPage>();
        goals.WaitForAssertion(() => goals.Find("input[aria-label='Target']"));

        goals.FindAll(".mud-input-adornment-start").Select(a => a.TextContent.Trim()).Should().Contain("$");
    }

    [Fact]
    public async Task Debt_plan_error_from_the_api_is_shown_in_the_form_and_the_rows_are_kept()
    {
        Arrange("""{"debts":["Provide 1-50 debts with non-negative balance."]}""");
        var cut = Render<DebtPage>();
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Add debt")));
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Add debt", StringComparison.Ordinal)).ClickAsync(new());

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Compare", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.Should().Contain("Provide 1-50 debts");
            cut.FindAll("input").Should().Contain(i => i.GetAttribute("value") == "New debt");
        });
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
