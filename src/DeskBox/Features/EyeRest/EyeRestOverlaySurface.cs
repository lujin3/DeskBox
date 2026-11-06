using Windows.UI;

namespace DeskBox.Features.EyeRest;

/// <summary>
/// How the break-overlay card paints its surface: in-app XAML acrylic (a
/// real blur of the overlay background behind the card) or a translucent
/// tinted fill — the app's embedded-mica approximation and the Solid
/// material both resolve here.
/// </summary>
public enum EyeRestOverlayCardMaterial
{
    Acrylic,
    Tinted
}

/// <summary>
/// One break's resolved overlay surface. The card follows the app window
/// material design language — the global material type, surface opacity and
/// material intensity run through the same visual calculator that paints
/// widget surfaces, the border through the widget border calculator, and the
/// text tiers and ring track follow the effective light/dark theme, so the
/// overlay reads as a floating widget surface instead of a divergent theme.
/// Resolved once per break by the settings coordinator (the same contract as
/// <see cref="EyeRestBreakBackground"/>): every monitor shares one surface
/// and appearance edits apply on the next break.
/// </summary>
public sealed record EyeRestOverlaySurface(
    bool IsDark,
    EyeRestOverlayCardMaterial CardMaterial,
    Color CardTintColor,
    double CardTintOpacity,
    double CardLuminosityOpacity,
    Color CardFallbackColor,
    double CardBorderThickness,
    Color CardBorderColor,
    Color DimColor,
    Color TitleColor,
    Color CountdownColor,
    Color TipColor,
    Color HintColor,
    Color RingTrackColor,
    Color RingArcColor);
