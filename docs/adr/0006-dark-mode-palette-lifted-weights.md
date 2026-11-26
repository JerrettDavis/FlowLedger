# ADR 0006 — Dark-Mode Palette Uses Lifted 400-Weight Variants

Status: Accepted

## Context
The FlowLedger UI redesign introduced a light and dark MudBlazor theme. The light palette uses navy/red semantic colors (Primary #1E40AF, Success #047857, Error #B91C1C, Warning #B45309). The original light values (Success #059669 at 3.77:1, Warning #D97706 at 3.19:1, secondary text #64748B at 4.34:1 on #F1F5F9) failed WCAG AA for text and were darkened (issue #200); every text-role colour now meets 4.5:1 on all three light surfaces. However, applying those same hues on the dark surface (#192134) fails WCAG AA contrast requirements: #1E40AF and #DC2626 both fall below the 4.5:1 ratio needed for normal text and below 3:1 for large/graphic elements when rendered on #192134.

## Decision
The dark-mode palette uses lifted 400-weight variants of the same hue families: Primary #60A5FA (blue-400), Success #34D399 (emerald-400), Error #F87171 (red-400), Warning #FBBF24 (amber-400). These lighter variants meet WCAG AA contrast on the dark surface while remaining visually consistent with the light palette's intent.

## Consequences
- Positive: semantic colors (success, error, warning) remain accessible in both light and dark modes without per-component overrides.
- Positive: the 400-weight variants read naturally as "the same color family, adapted for dark backgrounds" — no jarring hue shift.
- Negative: the dark Primary (#60A5FA) is noticeably lighter than the light Primary (#1E40AF); care is needed if the two themes are ever shown side-by-side in documentation.

## Contrast ratios (WCAG 2.x)
Enforced by `FlowLedgerThemeContrastTests`: every text-role colour must reach 4.5:1 on the surface, background and gray background of its palette.

| Palette | Colour | on Surface | on Background | on BackgroundGray |
|---|---|---|---|---|
| Light | Success #047857 | 5.48 | 5.24 | 5.01 |
| Light | Warning #B45309 | 5.02 | 4.80 | 4.58 |
| Light | Error #B91C1C | 6.47 | 6.18 | 5.91 |
| Light | Secondary #475569 | 7.58 | 7.24 | 6.92 |
| Light | Text secondary #566377 | 6.09 | 5.82 | 5.56 |
| Dark | Success #34D399 | 8.35 | 9.29 | 9.23 |
| Dark | Warning #FBBF24 | 9.61 | 10.69 | 10.63 |
| Dark | Error #F87171 | 5.80 | 6.45 | 6.41 |
| Dark | Text secondary #94A3B8 | 6.26 | 6.96 | 6.92 |
