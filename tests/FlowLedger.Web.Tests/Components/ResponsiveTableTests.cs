using System.Text.RegularExpressions;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

public sealed class ResponsiveTableTests
{
    // Inline styles bypass theming and block a strict CSP style-src. This is a ratchet: it may shrink, never grow.
    private const int InlineStyleBaseline = 104;

    [Fact]
    public void Tables_can_scroll_sideways_instead_of_clipping_columns()
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        var block = Regex.Match(css, @"\.mud-table-container,[^{]*\{(?<body>[^}]*)\}").Groups["body"].Value;
        block.Should().Contain("overflow-x: auto");
        block.Should().NotMatchRegex(@"overflow\s*:\s*hidden");
    }

    [Theory]
    [InlineData("fl-hide-lg", 1279)]
    [InlineData("fl-hide-md", 959)]
    public void Column_hiding_only_starts_below_laptop_widths(string cssClass, int maxWidth)
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        css.Should().MatchRegex($@"@media \(max-width: {maxWidth}\.98px\) \{{ \.{cssClass} \{{ display: none !important; \}} \}}");
    }

    [Fact]
    public void Razor_files_define_no_style_blocks()
    {
        foreach (var file in RepoFiles.Razor())
        {
            Regex.IsMatch(File.ReadAllText(file), @"<style[\s>]").Should().BeFalse($"{Path.GetFileName(file)} should use a CSS file");
        }
    }

    [Fact]
    public void Inline_style_attributes_do_not_grow()
    {
        var count = RepoFiles.Razor().Sum(f => Regex.Matches(File.ReadAllText(f), @"\sstyle=""").Count);

        count.Should().BeLessThanOrEqualTo(InlineStyleBaseline);
    }

    [Fact]
    public void Touch_devices_get_44px_icon_button_targets()
    {
        var css = RepoFiles.Read("wwwroot/app.css");

        css.Should().MatchRegex(@"@media \(pointer: coarse\)[^{]*\{[^}]*\.mud-icon-button[^}]*min-height: 44px");
    }
}
