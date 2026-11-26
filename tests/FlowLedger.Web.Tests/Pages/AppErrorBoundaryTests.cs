using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Pages;

public sealed class AppErrorBoundaryTests : BunitContext
{
    public AppErrorBoundaryTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Loader.Reset();
    }

    /// <summary>Stands in for a page whose API call fails in a lifecycle method.</summary>
    private sealed class Loader : ComponentBase
    {
        public static bool Fail { get; set; }
        public static int Calls { get; private set; }
        public static void Reset() { Fail = true; Calls = 0; }

        protected override Task OnInitializedAsync()
        {
            Calls++;
            return Fail ? throw new ApiClientException("API unreachable") : Task.CompletedTask;
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, "page loaded");
    }

    private IRenderedComponent<AppErrorBoundary> RenderBoundary() =>
        Render<AppErrorBoundary>(p => p.AddChildContent<Loader>());

    [Fact]
    public void Failing_loader_renders_an_inline_error_instead_of_escaping()
    {
        var cut = RenderBoundary();

        cut.Markup.Should().Contain("This page couldn't load");
        cut.Markup.Should().NotContain("page loaded");
        cut.Markup.Should().NotContain("API unreachable", "raw exception text must not reach the user");
    }

    [Fact]
    public void Retry_runs_the_loader_again_and_recovers()
    {
        var cut = RenderBoundary();
        var before = Loader.Calls;

        Loader.Fail = false;
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();

        Loader.Calls.Should().BeGreaterThan(before);
        cut.Markup.Should().Contain("page loaded");
    }

    [Fact]
    public void Navigating_elsewhere_clears_the_error()
    {
        var cut = RenderBoundary();
        cut.Markup.Should().Contain("This page couldn't load");

        Loader.Fail = false;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/budgets");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("page loaded"));
    }
}
