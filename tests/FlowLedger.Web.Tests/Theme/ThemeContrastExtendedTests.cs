using System.Text.RegularExpressions;
using FlowLedger.Web.Theme;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Utilities;

namespace FlowLedger.Web.Tests.Theme;

/// <summary>
/// Contrast ratios computed from the theme tokens (WCAG 2.x relative luminance), covering what the base text test does not:
/// text on filled controls, app bar and drawer chrome, UI component boundaries (3:1), and the CSS-only chart/muted tokens.
/// </summary>
public sealed class ThemeContrastExtendedTests
{
    private const double TextRatio = 4.5;
    private const double ComponentRatio = 3.0;

    private static double Luminance(MudColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Contrast of <paramref name="fg"/> over <paramref name="bg"/>; a translucent foreground is composited onto the background first.</summary>
    private static double Ratio(MudColor fg, MudColor bg)
    {
        var a = fg.A / 255.0;
        var composed = a >= 1
            ? fg
            : new MudColor(
                (byte)Math.Round(fg.R * a + bg.R * (1 - a)),
                (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
                (byte)Math.Round(fg.B * a + bg.B * (1 - a)),
                (byte)255);
        var (hi, lo) = (Math.Max(Luminance(composed), Luminance(bg)), Math.Min(Luminance(composed), Luminance(bg)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void Ratio_matches_known_wcag_reference_values()
    {
        Ratio(new MudColor("#000000"), new MudColor("#FFFFFF")).Should().BeApproximately(21.0, 0.01);
        Ratio(new MudColor("#777777"), new MudColor("#FFFFFF")).Should().BeApproximately(4.48, 0.02);
        Ratio(new MudColor("#FFFFFF"), new MudColor("#FFFFFF")).Should().BeApproximately(1.0, 0.001);
    }

    private static IEnumerable<(string Name, MudColor Fill, MudColor Text)> FilledControls(Palette p) =>
    [
        ("Primary", p.Primary, p.PrimaryContrastText),
        ("Secondary", p.Secondary, p.SecondaryContrastText),
        ("Success", p.Success, p.SuccessContrastText),
        ("Error", p.Error, p.ErrorContrastText),
        ("Warning", p.Warning, p.WarningContrastText),
        ("Info", p.Info, p.InfoContrastText),
    ];

    [Fact]
    public void Light_filled_buttons_and_chips_have_readable_label_text()
    {
        var p = FlowLedgerTheme.Instance.PaletteLight;

        var failures = FilledControls(p).Where(c => Ratio(c.Text, c.Fill) < TextRatio)
            .Select(c => $"light {c.Name} label {Ratio(c.Text, c.Fill):0.00}:1").ToList();

        failures.Should().BeEmpty();
    }

    [Fact]
    public void Dark_filled_buttons_and_chips_have_readable_label_text()
    {
        var p = FlowLedgerTheme.Instance.PaletteDark;

        var failures = FilledControls(p).Where(c => Ratio(c.Text, c.Fill) < TextRatio)
            .Select(c => $"dark {c.Name} label {Ratio(c.Text, c.Fill):0.00}:1").ToList();

        failures.Should().BeEmpty();
    }

    [Fact]
    public void Light_chrome_text_and_icons_are_readable()
    {
        var p = FlowLedgerTheme.Instance.PaletteLight;

        Ratio(p.AppbarText, p.AppbarBackground).Should().BeGreaterThanOrEqualTo(TextRatio, "app bar text");
        Ratio(p.DrawerText, p.DrawerBackground).Should().BeGreaterThanOrEqualTo(TextRatio, "drawer text");
        Ratio(p.DrawerIcon, p.DrawerBackground).Should().BeGreaterThanOrEqualTo(ComponentRatio, "drawer icons are graphics (3:1)");
        Ratio(p.ActionDefault, p.Surface).Should().BeGreaterThanOrEqualTo(ComponentRatio, "default icon buttons are graphics (3:1)");
    }

    [Fact]
    public void Dark_chrome_text_and_icons_are_readable()
    {
        var p = FlowLedgerTheme.Instance.PaletteDark;

        Ratio(p.AppbarText, p.AppbarBackground).Should().BeGreaterThanOrEqualTo(TextRatio, "app bar text");
        Ratio(p.DrawerText, p.DrawerBackground).Should().BeGreaterThanOrEqualTo(TextRatio, "drawer text");
        Ratio(p.DrawerIcon, p.DrawerBackground).Should().BeGreaterThanOrEqualTo(ComponentRatio, "drawer icons are graphics (3:1)");
    }

    [Fact]
    public void Input_borders_meet_the_3_to_1_component_contrast_in_both_themes()
    {
        var light = FlowLedgerTheme.Instance.PaletteLight;
        var dark = FlowLedgerTheme.Instance.PaletteDark;

        Ratio(light.LinesInputs, light.Surface).Should().BeGreaterThanOrEqualTo(ComponentRatio, "light input border on surface");
        Ratio(dark.LinesInputs, dark.Surface).Should().BeGreaterThanOrEqualTo(ComponentRatio, "dark input border on surface");
    }

    [Fact]
    public void Css_only_chart_and_muted_tokens_meet_AA_on_their_surface()
    {
        var css = RepoFiles.Read("wwwroot/app.css");
        var blocks = Regex.Matches(css, @"--fl-surface:\s*(?<surface>#[0-9a-fA-F]{6});\s*--fl-text:\s*(?<text>#[0-9a-fA-F]{6});\s*--fl-muted:\s*(?<muted>#[0-9a-fA-F]{6});");
        blocks.Should().HaveCount(2, "one light and one dark token block");

        foreach (Match m in blocks)
        {
            var surface = new MudColor(m.Groups["surface"].Value);
            Ratio(new MudColor(m.Groups["text"].Value), surface).Should().BeGreaterThanOrEqualTo(TextRatio, $"--fl-text on {m.Groups["surface"].Value}");
            Ratio(new MudColor(m.Groups["muted"].Value), surface).Should().BeGreaterThanOrEqualTo(TextRatio, $"--fl-muted on {m.Groups["surface"].Value}");
        }
    }

    [Fact]
    public void Semantic_text_colours_also_pass_on_tinted_alert_and_hover_backgrounds()
    {
        foreach (var (name, p) in new[] { ("light", (Palette)FlowLedgerTheme.Instance.PaletteLight), ("dark", FlowLedgerTheme.Instance.PaletteDark) })
        {
            var hover = p.TableHover;
            foreach (var (label, fg) in new[] { ("TextPrimary", p.TextPrimary), ("TextSecondary", p.TextSecondary), ("Primary", p.Primary) })
            {
                Ratio(fg, Over(hover, p.Surface)).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} {label} on table hover");
            }
        }
    }

    [Fact]
    public void Muted_secondary_text_passes_on_every_surface_it_is_drawn_on_in_both_themes()
    {
        // .fl-muted, captions and list sub-lines all resolve to TextSecondary (axe color-contrast findings: it was 3.1:1 light, 3.8:1 dark).
        foreach (var (name, p) in new[] { ("light", (Palette)FlowLedgerTheme.Instance.PaletteLight), ("dark", FlowLedgerTheme.Instance.PaletteDark) })
        {
            var current = Over(new MudColor(p.Primary.R, p.Primary.G, p.Primary.B, (byte)Math.Round(255 * 0.08)), p.Surface);
            foreach (var (surface, bg) in new[]
                     {
                         ("Surface", p.Surface), ("Background", p.Background), ("BackgroundGray", p.BackgroundGray),
                         ("current-month column tint", current), ("table hover", Over(p.TableHover, p.Surface)),
                     })
            {
                Ratio(p.TextSecondary, bg).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} TextSecondary on {surface}");
                Ratio(p.Secondary, bg).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} Secondary (Color.Secondary text) on {surface}");
            }
        }
    }

    [Fact]
    public void Links_and_money_plan_status_cells_pass_AA()
    {
        foreach (var (name, p) in new[] { ("light", (Palette)FlowLedgerTheme.Instance.PaletteLight), ("dark", FlowLedgerTheme.Instance.PaletteDark) })
        {
            Ratio(p.Primary, p.Surface).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} link (Primary) on Surface");
            Ratio(p.Primary, p.Background).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} link (Primary) on Background");

            // .fl-sheet-cell.paid / .late: text is color-mix(status 60%, TextPrimary) on a 12%/14% status tint over the current-column tint.
            var current = Over(new MudColor(p.Primary.R, p.Primary.G, p.Primary.B, (byte)Math.Round(255 * 0.08)), p.Surface);
            foreach (var (status, color, tint) in new[] { ("paid", p.Success, 0.12), ("late", p.Warning, 0.14) })
            {
                var text = Mix(color, p.TextPrimary, 0.6);
                var bg = Over(new MudColor(color.R, color.G, color.B, (byte)Math.Round(255 * tint)), current);
                Ratio(text, bg).Should().BeGreaterThanOrEqualTo(TextRatio, $"{name} sheet cell {status}");
            }
        }
    }

    [Fact]
    public void Muted_text_is_coloured_not_faded_with_opacity()
    {
        // opacity composites the secondary colour toward the background and silently drops it under 4.5:1; use the colour token.
        var css = RepoFiles.Read("wwwroot/finance-ui.css");
        foreach (var selector in new[] { ".fl-muted", ".fl-li__sub", ".fl-tile__label", ".fl-account-chip__bal" })
        {
            var rule = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
            rule.Success.Should().BeTrue($"{selector} is defined");
            rule.Groups["body"].Value.Should().NotContain("opacity", $"{selector} must use text-secondary, not opacity");
            rule.Groups["body"].Value.Should().Contain("--mud-palette-text-secondary");
        }
    }

    private static MudColor Mix(MudColor a, MudColor b, double weightA) => new(
        (byte)Math.Round(a.R * weightA + b.R * (1 - weightA)),
        (byte)Math.Round(a.G * weightA + b.G * (1 - weightA)),
        (byte)Math.Round(a.B * weightA + b.B * (1 - weightA)),
        (byte)255);

    private static MudColor Over(MudColor tint, MudColor bg)
    {
        var a = tint.A / 255.0;
        return new MudColor(
            (byte)Math.Round(tint.R * a + bg.R * (1 - a)),
            (byte)Math.Round(tint.G * a + bg.G * (1 - a)),
            (byte)Math.Round(tint.B * a + bg.B * (1 - a)),
            (byte)255);
    }

    /// <summary>The pos/neg pills (finance-ui.css) are the status colour over a 14% tint of itself, over the surface: axe measured 4.49:1 on the old green.</summary>
    [Fact]
    public void Status_pills_are_readable_on_their_own_tint_in_both_themes()
    {
        static MudColor Tint(MudColor status, MudColor surface, double share) => new(
            (byte)Math.Round(status.R * share + surface.R * (1 - share)),
            (byte)Math.Round(status.G * share + surface.G * (1 - share)),
            (byte)Math.Round(status.B * share + surface.B * (1 - share)),
            (byte)255);

        var light = FlowLedgerTheme.Instance.PaletteLight;
        var dark = FlowLedgerTheme.Instance.PaletteDark;
        var cases = new (string Name, MudColor Text, MudColor Surface)[]
        {
            ("light success pill", light.Success, light.Surface),
            ("light error pill", light.Error, light.Surface),
            ("dark success pill", dark.Success, dark.Surface),
            ("dark error pill", dark.Error, dark.Surface),
        };

        var failures = cases
            .Select(c => (c.Name, Ratio: Ratio(c.Text, Tint(c.Text, c.Surface, 0.14))))
            .Where(c => c.Ratio < TextRatio)
            .Select(c => $"{c.Name} {c.Ratio:0.00}:1")
            .ToList();

        failures.Should().BeEmpty();
    }
}
