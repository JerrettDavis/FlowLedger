using System.Net;
using System.Reflection;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>
/// Every routable page (found by reflecting over <see cref="RouteAttribute"/>) must render against a fake API
/// in the empty and failing states without throwing. New pages are covered automatically.
/// </summary>
public sealed class RouteSmokeTests : BunitContext, IAsyncLifetime
{
    public static IEnumerable<object[]> Pages() =>
        typeof(FlowLedger.Web.Components.Routes).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RouteAttribute>().Any())
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t.Name });

    private static Type PageType(string name) =>
        typeof(FlowLedger.Web.Components.Routes).Assembly.GetTypes().Single(t => t.Name == name && typeof(IComponent).IsAssignableFrom(t));

    private sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private void Arrange(HttpStatusCode code, string body)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler(code, body)) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        Services.AddScoped<ThemeService>();
        Services.AddScoped<DisplaySettingsService>();
        Services.AddSingleton(new FlowLedger.Web.Auth.OidcOptions());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Services.AddSingleton(_ => new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance));
        Services.AddSingleton(sp => sp.GetRequiredService<ComponentStatePersistenceManager>().State);
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_renders_when_the_api_returns_nothing(string page)
    {
        Arrange(HttpStatusCode.OK, "null");

        var act = () => Render(PageFragment(PageType(page)));

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_renders_when_the_api_is_failing(string page)
    {
        Arrange(HttpStatusCode.InternalServerError, "{}");

        var act = () => Render(PageFragment(PageType(page)));

        act.Should().NotThrow();
    }

    private static RenderFragment PageFragment(Type page) => b =>
    {
        b.OpenComponent(0, page);
        b.CloseComponent();
    };

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
