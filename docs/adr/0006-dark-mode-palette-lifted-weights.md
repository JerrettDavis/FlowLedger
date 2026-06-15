# ADR 0006 — Dark-Mode Palette Uses Lifted 400-Weight Variants

## Context
The FlowLedger UI redesign introduced a light and dark MudBlazor theme. The light palette uses navy/red semantic colors (Primary #1E40AF, Success #059669, Error #DC2626, Warning #D97706) which have adequate contrast against a white (#FFFFFF) surface. However, applying those same hues on the dark surface (#192134) fails WCAG AA contrast requirements: #1E40AF and #DC2626 both fall below the 4.5:1 ratio needed for normal text and below 3:1 for large/graphic elements when rendered on #192134.

## Decision
The dark-mode palette uses lifted 400-weight variants of the same hue families: Primary #60A5FA (blue-400), Success #34D399 (emerald-400), Error #F87171 (red-400), Warning #FBBF24 (amber-400). These lighter variants meet WCAG AA contrast on the dark surface while remaining visually consistent with the light palette's intent.

## Consequences
- Positive: semantic colors (success, error, warning) remain accessible in both light and dark modes without per-component overrides.
- Positive: the 400-weight variants read naturally as "the same color family, adapted for dark backgrounds" — no jarring hue shift.
- Negative: the dark Primary (#60A5FA) is noticeably lighter than the light Primary (#1E40AF); care is needed if the two themes are ever shown side-by-side in documentation.
