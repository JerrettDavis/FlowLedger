using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Layout;
using FlowLedger.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class MainLayoutTests : BunitContext, IAsyncLifetime
{
    private sealed class CountingHandler(string environment) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var json = request.RequestUri!.AbsolutePath == "/api/environment"
                ? $$"""{"environment":"{{environment}}","isProduction":{{(environment == "production" ? "true" : "false")}}}"""
                : """{"environment":"x","backup":{"healthy":true,"stale":false,"offsiteConfigured":true,"lastSuccessUtc":null,"lastRun":null,"runs":[]},"providerUsage":{"provider":"x","callsToday":0,"callsThisMonth":0,"dailyBudget":1,"monthlyBudget":1,"paused":false,"byEndpoint":[]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private CountingHandler Arrange(string environment = "demo")
    {
        var handler = new CountingHandler(environment);
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        Services.AddScoped<ThemeService>();
        Services.AddSingleton(new FlowLedger.Web.Auth.OidcOptions());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Services.AddSingleton(_ => new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance));
        Services.AddSingleton(sp => sp.GetRequiredService<ComponentStatePersistenceManager>().State);
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        return handler;
    }

    private IRenderedComponent<MainLayout> RenderLayout() => Render<MainLayout>(p => p.Add(l => l.Body, b => b.AddContent(0, "body")));

    [Fact]
    public void Sync_indicator_is_a_polite_status_live_region_even_while_idle()
    {
        Arrange();

        var cut = RenderLayout();

        var status = cut.Find("[data-testid=sync-status]");
        status.GetAttribute("role").Should().Be("status");
        status.GetAttribute("aria-live").Should().Be("polite");
    }

    [Fact]
    public void Layout_issues_one_call_per_status_endpoint_and_shows_the_demo_action_once_known()
    {
        var handler = Arrange("demo");

        var cut = RenderLayout();
        cut.WaitForState(() => cut.Markup.Contains("Connect and sync demo data"));

        handler.Calls.Should().Be(2);
        cut.Markup.Should().NotContain("Connect bank");
    }

    [Fact]
    public void Production_shows_connect_bank_never_the_demo_button()
    {
        Arrange("production");

        var cut = RenderLayout();
        cut.WaitForState(() => cut.Markup.Contains("Connect bank"));

        cut.Markup.Should().NotContain("Connect and sync demo data");
    }

    [Fact]
    public void Skip_link_targets_a_focusable_main_landmark()
    {
        Arrange();

        var cut = RenderLayout();

        cut.Find("a.fl-skip-link").GetAttribute("href").Should().Be("#main-content");
        var main = cut.Find("main#main-content");
        main.GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void Landmark_names_are_unique()
    {
        Arrange();

        var cut = RenderLayout();

        var names = cut.FindAll("nav[aria-label], aside[aria-label], header[aria-label], main[aria-label]")
            .Select(e => $"{e.LocalName}:{e.GetAttribute("aria-label")}")
            .ToList();
        names.Should().OnlyHaveUniqueItems();
        cut.FindAll("[aria-label='Main navigation']").Should().HaveCount(1);
    }

    // MudBlazor services are async-disposable only, so the context must be disposed asynchronously.
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
