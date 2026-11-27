using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Layout;
using FlowLedger.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class AppBarTests : BunitContext, IAsyncLifetime
{
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") });
    }

    private sealed class StatusHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private IRenderedComponent<AppBar> RenderBar(string environment = "production", HttpMessageHandler? handler = null)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(handler ?? new OkHandler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        Services.AddScoped<ThemeService>();
        Services.AddScoped<SyncCoordinator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        return Render<AppBar>(p => p
            .Add(b => b.Status, new LayoutStatus(environment, null))
            .Add(b => b.IsOwner, true));
    }

    [Fact]
    public void Share_and_theme_toggles_are_inline_on_wide_screens_and_collapse_into_one_overflow_menu_on_phones()
    {
        var cut = RenderBar();

        cut.Find("span.fl-appbar-inline button[aria-label='Toggle share mode']").Should().NotBeNull();
        cut.Find("span.fl-appbar-overflow button[aria-label='More actions']").Should().NotBeNull();
    }

    [Fact]
    public void Production_header_shows_when_the_last_sync_happened()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-12).ToString("O");
        var cut = RenderBar(handler: new StatusHandler("{\"lastSyncedAt\":\"" + at + "\",\"minIntervalMinutes\":30,\"provider\":\"Mx\"}"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='last-synced']").TextContent.Should().Contain("Synced 12 min ago"));
    }

    [Fact]
    public void Header_has_no_last_synced_text_before_the_first_sync()
    {
        RenderBar().FindAll("[data-testid='last-synced']").Should().BeEmpty();
    }

    [Theory]
    [InlineData(10, "Synced just now")]
    [InlineData(300, "Synced 5 min ago")]
    [InlineData(7200, "Synced 2 h ago")]
    [InlineData(172800, "Synced 2 d ago")]
    public void Sync_age_is_described_in_plain_language(int secondsAgo, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        FlowLedger.Web.Shared.SyncAge.Describe(now.AddSeconds(-secondsAgo), now).Should().Be(expected);
    }

    [Fact]
    public void Sync_actions_stay_directly_reachable_on_every_width()
    {
        var cut = RenderBar();

        cut.Find("button[aria-label='Sync now']").Closest(".fl-appbar-inline").Should().BeNull();
        cut.Find("button[aria-label='Connect bank']").Closest(".fl-appbar-inline").Should().BeNull();
    }

    [Fact]
    public void The_phone_layout_css_hides_the_inline_group_and_shows_the_overflow_menu()
    {
        var css = RepoFiles.Read("wwwroot/finance-ui.css");

        css.Should().MatchRegex(@"@media \(max-width: 599\.98px\) \{[^}]*html \.fl-appbar-inline \{ display: none; \}");
        css.Should().Contain(".fl-appbar-overflow { display: none; }");
    }

    [Fact]
    public void MainLayout_stays_slim()
    {
        RepoFiles.Read("Components/Layout/MainLayout.razor").Split('\n').Length.Should().BeLessThan(250);
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
