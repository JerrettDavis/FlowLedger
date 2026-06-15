using MudBlazor;

namespace FlowLedger.Web.Theme;

/// <summary>
/// The FlowLedger theme — light and dark palettes, typography, and layout properties.
/// Centered on blue/slate/emerald with refined contrast and accessibility.
/// </summary>
public static class FlowLedgerTheme
{
    public static readonly MudTheme Instance = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1E40AF",
            Secondary = "#64748B",
            Tertiary = "#059669",
            Success = "#059669",
            Error = "#DC2626",
            Warning = "#D97706",
            Info = "#1E40AF",
            Background = "#F8FAFC",
            BackgroundGray = "#F1F5F9",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#0F172A",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#0F172A",
            DrawerIcon = "#64748B",
            TextPrimary = "#0F172A",
            TextSecondary = "#64748B",
            TextDisabled = "rgba(15,23,42,0.38)",
            ActionDefault = "#64748B",
            ActionDisabled = "rgba(15,23,42,0.26)",
            ActionDisabledBackground = "rgba(15,23,42,0.12)",
            Divider = "#E2E8F0",
            DividerLight = "#EEF2F6",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#CBD5E1",
            TableLines = "#E2E8F0",
            TableStriped = "rgba(15,23,42,0.02)",
            TableHover = "rgba(30,64,175,0.05)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#60A5FA",
            Secondary = "#94A3B8",
            Tertiary = "#34D399",
            Success = "#34D399",
            Error = "#F87171",
            Warning = "#FBBF24",
            Info = "#60A5FA",
            Background = "#0F172A",
            BackgroundGray = "#111827",
            Surface = "#192134",
            AppbarBackground = "#192134",
            AppbarText = "#F1F5F9",
            DrawerBackground = "#192134",
            DrawerText = "#E2E8F0",
            DrawerIcon = "#94A3B8",
            TextPrimary = "#F1F5F9",
            TextSecondary = "#94A3B8",
            Divider = "rgba(255,255,255,0.08)",
            LinesDefault = "rgba(255,255,255,0.08)",
            LinesInputs = "rgba(255,255,255,0.15)",
            TableLines = "rgba(255,255,255,0.08)",
            TableStriped = "rgba(255,255,255,0.02)",
            TableHover = "rgba(96,165,250,0.08)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "14px",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            H1 = new H1Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "2.5rem",
                FontWeight = "700",
                LineHeight = "1.2",
            },
            H2 = new H2Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "2rem",
                FontWeight = "700",
                LineHeight = "1.25",
            },
            H3 = new H3Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "1.75rem",
                FontWeight = "600",
                LineHeight = "1.3",
            },
            H4 = new H4Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "1.5rem",
                FontWeight = "600",
                LineHeight = "1.3",
            },
            H5 = new H5Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "1.25rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            H6 = new H6Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "1.0625rem",
                FontWeight = "600",
                LineHeight = "1.4",
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "1rem",
                FontWeight = "500",
                LineHeight = "1.5",
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.875rem",
                FontWeight = "600",
                LineHeight = "1.5",
            },
            Body1 = new Body1Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.9375rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Body2 = new Body2Typography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
            },
            Button = new ButtonTypography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.875rem",
                FontWeight = "500",
                LineHeight = "1.75",
                LetterSpacing = "0",
            },
            Caption = new CaptionTypography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.75rem",
                FontWeight = "400",
                LineHeight = "1.4",
            },
            Overline = new OverlineTypography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.6875rem",
                FontWeight = "600",
                LineHeight = "1.6",
                LetterSpacing = "0.08em",
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            DrawerWidthLeft = "256px",
            AppbarHeight = "64px",
        },
    };
}
