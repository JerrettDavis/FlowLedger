using FlowLedger.Web.Theme;
using FluentAssertions;
using MudBlazor.Utilities;

namespace FlowLedger.Web.Tests.Theme;

/// <summary>WCAG 2.x AA: text colours need 4.5:1 against every surface they can sit on (ADR 0006).</summary>
public sealed class FlowLedgerThemeContrastTests
{
    private const double MinimumTextRatio = 4.5;

    private static double Luminance(MudColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Ratio(MudColor a, MudColor b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static void AssertTextContrast(string palette, (string Name, MudColor Color)[] text, (string Name, MudColor Color)[] surfaces)
    {
        var failures = (from t in text
                        from s in surfaces
                        let r = Ratio(t.Color, s.Color)
                        where r < MinimumTextRatio
                        select $"{palette} {t.Name} on {s.Name}: {r:0.00}:1").ToList();

        failures.Should().BeEmpty("text roles must reach 4.5:1 on every surface");
    }

    [Fact]
    public void Light_palette_text_roles_meet_AA_on_every_surface()
    {
        var p = FlowLedgerTheme.Instance.PaletteLight;
        AssertTextContrast("light",
            [("Primary", p.Primary), ("Secondary", p.Secondary), ("Success", p.Success), ("Error", p.Error),
             ("Warning", p.Warning), ("Info", p.Info), ("TextPrimary", p.TextPrimary), ("TextSecondary", p.TextSecondary)],
            [("Surface", p.Surface), ("Background", p.Background), ("BackgroundGray", p.BackgroundGray)]);
    }

    [Fact]
    public void Dark_palette_text_roles_meet_AA_on_every_surface()
    {
        var p = FlowLedgerTheme.Instance.PaletteDark;
        AssertTextContrast("dark",
            [("Primary", p.Primary), ("Secondary", p.Secondary), ("Success", p.Success), ("Error", p.Error),
             ("Warning", p.Warning), ("Info", p.Info), ("TextPrimary", p.TextPrimary), ("TextSecondary", p.TextSecondary)],
            [("Surface", p.Surface), ("Background", p.Background), ("BackgroundGray", p.BackgroundGray)]);
    }
}
