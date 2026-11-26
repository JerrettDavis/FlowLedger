using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

public sealed class KeyboardAccessTests
{
    [Fact]
    public void Global_css_defines_a_focus_visible_ring_with_a_real_outline()
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        css.Should().MatchRegex(@":focus-visible[^{]*\{[^}]*outline:\s*2px solid");
    }

    [Fact]
    public void Pointer_only_column_drag_reordering_is_not_enabled_on_data_grids()
    {
        foreach (var file in RepoFiles.Razor())
        {
            File.ReadAllText(file).Should().NotContain("DragDropColumnReordering=\"true\"", $"{Path.GetFileName(file)} would be pointer-only");
        }
    }

    [Fact]
    public void Global_css_hides_the_skip_link_until_it_is_focused()
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        css.Should().Contain(".fl-skip-link:focus");
    }
}
