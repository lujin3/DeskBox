using Windows.UI;

namespace DeskBox.Features.EyeRest;

/// <summary>
/// One break's resolved overlay surface. The window paints the raw user
/// background (or solid color) under Focust's slate-950/35 scrim with a
/// frosted-glass card as fixed presentation (PromptApp.vue); this record
/// carries only the settings-influenced part — the effective theme (for the
/// templated button states) and the Focust tier colors: white text family
/// (the scrim is dark in both themes, so dark ink text would sink into it),
/// faint ring track, progress arc in the same white family. Resolved once
/// per break by the settings coordinator (the same contract as
/// <see cref="EyeRestBreakBackground"/>): every monitor shares one surface
/// and theme edits land on the next break.
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
