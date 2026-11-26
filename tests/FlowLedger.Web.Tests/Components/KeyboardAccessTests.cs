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
    public void Click_handlers_on_non_interactive_elements_are_keyboard_operable()
    {
        var re = new System.Text.RegularExpressions.Regex(@"<(div|span|tr|td|li|MudPaper|MudCard)\b[^<]*?@onclick(?!:)[^<]*?(?=\r?\n\s*<|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (var file in RepoFiles.Razor())
        {
            foreach (System.Text.RegularExpressions.Match m in re.Matches(File.ReadAllText(file)))
            {
                m.Value.Should().Contain("role=\"button\"", $"{Path.GetFileName(file)} has a pointer-only click target");
                m.Value.Should().Contain("tabindex=\"0\"").And.Contain("@onkeydown");
            }
        }
    }

    [Fact]
    public void Global_css_hides_the_skip_link_until_it_is_focused()
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        css.Should().Contain(".fl-skip-link:focus");
    }
}
