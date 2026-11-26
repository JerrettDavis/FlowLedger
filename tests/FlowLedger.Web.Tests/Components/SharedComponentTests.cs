using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FlowLedger.Web.Components.Shared;
using FlowLedger.Web.Shared;
using FlowLedger.Web.Tests.ApiClient;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>bUnit coverage for the reusable UI building blocks (the UX audit's regression guardrail).</summary>
public sealed class SharedComponentTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public SharedComponentTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
    }

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void CardSkeleton_renders_Count_placeholders_with_the_requested_height()
    {
        var cut = _ctx.Render<CardSkeleton>(p => p.Add(c => c.Count, 3).Add(c => c.Height, "40px"));

        var items = cut.FindAll(".fl-skeleton");
        items.Should().HaveCount(3);
        items.Should().OnlyContain(e => e.GetAttribute("style")!.Contains("height:40px"));
    }

    [Fact]
    public void CardSkeleton_defaults_to_a_single_80px_placeholder()
    {
        var cut = _ctx.Render<CardSkeleton>();

        cut.FindAll(".fl-skeleton").Should().ContainSingle()
            .Which.GetAttribute("style").Should().Contain("height:80px");
    }

    [Fact]
    public void EmptyState_shows_title_and_description_but_no_action_when_not_supplied()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(c => c.Icon, Icons.Material.Filled.Inbox)
            .Add(c => c.Title, "Nothing here yet")
            .Add(c => c.Description, "Connect a bank to get started."));

        cut.Markup.Should().Contain("Nothing here yet").And.Contain("Connect a bank to get started.");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void EmptyState_omits_the_description_paragraph_when_empty()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(c => c.Icon, Icons.Material.Filled.Inbox)
            .Add(c => c.Title, "Empty"));

        cut.FindAll(".mud-typography-body2").Should().BeEmpty("no description is rendered");
    }

    [Fact]
    public void EmptyState_renders_the_action_fragment()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(c => c.Icon, Icons.Material.Filled.Inbox)
            .Add(c => c.Title, "Empty")
            .Add(c => c.Action, "<button id=\"go\">Go</button>"));

        cut.Find("#go").TextContent.Should().Be("Go");
    }

    [Fact]
    public void CategoryChip_without_a_handler_is_a_non_interactive_span()
    {
        var cut = _ctx.Render<CategoryChip>(p => p.Add(c => c.Path, "Food/Groceries"));

        cut.FindAll("button").Should().BeEmpty();
        cut.Find(".fl-catchip-leaf").TextContent.Should().Be("Groceries");
        cut.Find(".fl-catchip-parent").TextContent.Should().Contain("Food");
    }

    [Fact]
    public void CategoryChip_with_a_handler_is_a_button_that_invokes_it()
    {
        var clicked = 0;
        var cut = _ctx.Render<CategoryChip>(p => p
            .Add(c => c.Path, "Food/Groceries")
            .Add(c => c.AriaLabel, "Change category")
            .Add(c => c.OnClick, () => clicked++));

        var button = cut.Find("button");
        button.GetAttribute("aria-label").Should().Be("Change category");
        button.Click();

        clicked.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Uncategorized")]
    public void CategoryChip_treats_blank_and_Uncategorized_as_uncategorized(string? path)
    {
        var cut = _ctx.Render<CategoryChip>(p => p.Add(c => c.Path, path).Add(c => c.OnClick, () => { }));

        cut.Find("button").ClassList.Should().Contain("fl-catchip-empty");
        cut.Find(".fl-catchip-leaf").TextContent.Should().Be("Categorize");
        cut.FindAll(".fl-catchip-dot").Should().BeEmpty();
    }

    [Fact]
    public void ResultTable_renders_headers_rows_and_right_aligns_numbers()
    {
        var result = new QueryResultDto(
            [new QueryColumnDto("Merchant", "text"), new QueryColumnDto("Total", "number")],
            [[Json("\"Coffee\""), Json("1234.5")]],
            false,
            1);

        var cut = _ctx.Render<ResultTable>(p => p.Add(c => c.Result, result));

        cut.FindAll("th[scope=col]").Select(e => e.TextContent).Should().Equal("Merchant", "Total");
        cut.FindAll("td").Select(e => e.TextContent).Should().Equal("Coffee", 1234.5m.ToString("N2", CultureInfo.InvariantCulture));
        cut.FindAll("td")[1].ClassList.Should().Contain("num");
    }

    [Fact]
    public void ResultTable_caps_rows_and_says_so()
    {
        var rows = Enumerable.Range(0, 5).Select(i => new List<JsonElement> { Json(i.ToString(CultureInfo.InvariantCulture)) }).ToList();
        var result = new QueryResultDto([new QueryColumnDto("n", "number")], rows, false, 1);

        var cut = _ctx.Render<ResultTable>(p => p.Add(c => c.Result, result).Add(c => c.MaxRows, 2));

        cut.FindAll("tbody tr").Should().HaveCount(2);
        cut.Markup.Should().Contain("Showing 2 of 5 rows.");
    }

    [Fact]
    public void ResultTable_shows_No_rows_for_an_empty_result()
    {
        var result = new QueryResultDto([new QueryColumnDto("n", "number")], [], false, 1);

        var cut = _ctx.Render<ResultTable>(p => p.Add(c => c.Result, result));

        cut.Markup.Should().Contain("No rows.");
    }

    [Fact]
    public void DateRangeSelector_Resolve_maps_presets_to_windows()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        DateRangeSelector.Resolve("Last 30 days").Should().Be((today.AddDays(-29), today));
        DateRangeSelector.Resolve("All time").Should().Be((null, today));
        DateRangeSelector.Resolve("Custom", new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1))
            .Should().Be((new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1)));
    }

    [Fact]
    public void DateRangeSelector_renders_without_pickers_unless_Custom()
    {
        var cut = _ctx.Render<DateRangeSelector>(p => p.Add(c => c.Preset, "Last 30 days"));
        _ctx.Render<MudPopoverProvider>();

        cut.FindComponents<MudDatePicker>().Should().BeEmpty();

        var custom = _ctx.Render<DateRangeSelector>(p => p.Add(c => c.Preset, DateRangeSelector.Custom));
        custom.FindComponents<MudDatePicker>().Should().HaveCount(2);
    }

    [Fact]
    public void AlertsPanel_renders_nothing_when_there_are_no_alerts()
    {
        _ctx.Services.AddSingleton(Client("[]"));

        var cut = _ctx.Render<AlertsPanel>();

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void AlertsPanel_renders_alerts_in_a_labelled_region()
    {
        _ctx.Services.AddSingleton(Client("""[{"severity":"error","title":"Low balance","detail":"Checking is under $100"}]"""));

        var cut = _ctx.Render<AlertsPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=region]").GetAttribute("aria-label").Should().Be("Alerts");
            cut.Markup.Should().Contain("Low balance").And.Contain("Checking is under $100");
        });
    }

    [Fact]
    public void AlertsPanel_failure_shows_an_announced_retry_instead_of_silence()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        _ctx.Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new StubHttpMessageHandler(response)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));

        var cut = _ctx.Render<AlertsPanel>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.Should().Contain("Couldn't load alerts");
            cut.Find("button").TextContent.Should().Contain("Retry");
        });
    }

    [Fact]
    public void DashboardCard_loading_state_shows_a_skeleton_and_marks_the_card_busy()
    {
        var cut = _ctx.Render<DashboardCard>(p => p.Add(c => c.Title, "Spending").Add(c => c.State, DashboardCardState.Loading)
            .AddChildContent("<p id=\"body\">ready</p>"));

        cut.Find(".fl-skeleton").Should().NotBeNull();
        cut.Find("[aria-busy=true]").Should().NotBeNull();
        cut.FindAll("#body").Should().BeEmpty();
    }

    [Fact]
    public void DashboardCard_error_state_is_an_alert_and_retry_invokes_the_callback()
    {
        var retried = 0;
        var cut = _ctx.Render<DashboardCard>(p => p.Add(c => c.Title, "Spending").Add(c => c.State, DashboardCardState.Error)
            .Add(c => c.OnRetry, EventCallback.Factory.Create(this, () => retried++)));

        cut.Find("[role=alert]").TextContent.Should().Contain("Couldn't load spending");
        cut.Find("button").Click();

        retried.Should().Be(1);
    }

    [Fact]
    public void DashboardCard_empty_state_shows_its_message_and_ready_shows_content()
    {
        var empty = _ctx.Render<DashboardCard>(p => p.Add(c => c.Title, "Spending").Add(c => c.State, DashboardCardState.Empty).Add(c => c.EmptyText, "No spending yet."));
        empty.Markup.Should().Contain("No spending yet.");

        var ready = _ctx.Render<DashboardCard>(p => p.Add(c => c.Title, "Spending").AddChildContent("<p id=\"body\">ready</p>"));
        ready.Find("#body").TextContent.Should().Be("ready");
    }

    private static FlowLedgerApiClient Client(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var http = new HttpClient(new StubHttpMessageHandler(response)) { BaseAddress = new Uri("http://localhost") };
        return new FlowLedgerApiClient(http, NullLogger<FlowLedgerApiClient>.Instance);
    }
}
