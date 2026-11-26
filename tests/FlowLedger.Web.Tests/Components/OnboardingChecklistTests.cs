using Bunit;
using FlowLedger.Web.Components.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class OnboardingChecklistTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public OnboardingChecklistTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
    }

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    [Fact]
    public void Checklist_is_a_labelled_region_with_four_setup_links()
    {
        var cut = _ctx.Render<OnboardingChecklist>();

        cut.Find("[role=region]").GetAttribute("aria-label").Should().Be("Get started");
        cut.FindAll("ol li a").Select(a => a.GetAttribute("href"))
            .Should().Equal("accounts", "transactions", "recurring-flows", "budgets");
    }

    [Fact]
    public void Checklist_copy_does_not_reference_environment_specific_toolbar_buttons()
    {
        var cut = _ctx.Render<OnboardingChecklist>();

        cut.Markup.Should().NotContain("Connect &amp; Sync").And.NotContain("demo data");
    }
}
