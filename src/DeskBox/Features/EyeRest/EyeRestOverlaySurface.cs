using Windows.UI;

namespace DeskBox.Features.EyeRest;

/// <summary>
/// One break's resolved overlay surface. The window paints the raw user
/// background (or solid color) under the slate-950/35 scrim with a
/// frosted-glass card; the countdown core is a HUD whose progress family is
/// cyan 00E5FF — a faint cyan track under the full-cyan arc. This record
/// carries only the settings-influenced part — the effective theme (for the
/// templated button states) and the tier colors: white text family (the
/// scrim is dark in both themes, so dark ink text would sink into it), the
/// cyan ring track and arc. Resolved once per break by the settings
/// coordinator (the same contract as <see cref="EyeRestBreakBackground"/>):
/// every monitor shares one surface and theme edits land on the next break.
/// </summary>
public sealed record EyeRestOverlaySurface(
    bool IsDark,
    Color DimColor,
    Color TitleColor,
    Color CountdownColor,
    Color TipColor,
    Color HintColor,
    Color RingTrackColor,
    Color RingArcColor);
