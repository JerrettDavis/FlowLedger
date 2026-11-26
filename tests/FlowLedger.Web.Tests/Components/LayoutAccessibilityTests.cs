using Bunit;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

/// <summary>Accessibility contracts for the layout chrome (#217, #218).</summary>
public sealed class LayoutAccessibilityTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    [Fact]
    public void ReconnectModal_status_text_is_a_polite_live_region()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = _ctx.Render<FlowLedger.Web.Components.Layout.ReconnectModal>();

        var live = cut.Find(".components-reconnect-container");
        live.GetAttribute("role").Should().Be("status");
        live.GetAttribute("aria-live").Should().Be("polite");
    }

    [Fact]
    public void App_css_defines_focus_visible_skip_link_and_scrollable_tables()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FlowLedger.Web", "wwwroot", "app.css"));

        css.Should().Contain(":focus-visible");
        css.Should().Contain(".fl-skip-link");
        css.Should().NotContain("overflow: hidden !important", "tables must scroll instead of clipping data");
    }

    [Fact]
    public void MainLayout_has_a_skip_link_target_and_announces_sync_as_status()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FlowLedger.Web", "Components", "Layout");
        var layout = File.ReadAllText(Path.Combine(dir, "MainLayout.razor"));
        var appBar = File.ReadAllText(Path.Combine(dir, "AppBar.razor"));

        layout.Should().Contain("href=\"#main-content\"");
        layout.Should().Contain("id=\"main-content\"");
        appBar.Should().Contain("role=\"status\"");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
