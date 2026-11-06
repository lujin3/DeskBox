using Windows.UI;

namespace DeskBox.Features.EyeRest;

/// <summary>
/// Visual palette of one break-overlay theme: every brush the overlay paints
/// with (card surface, dim layer, text tiers, ring, button states) resolved up
/// front so the window constructor stays a pure layout pass. Themes restyle
/// the card and accents only — the background preference (solid/image/folder)
/// stays independent, so any theme works over any background.
/// </summary>
public sealed record EyeRestOverlayThemePalette(
    string Name,
    bool HasCard,
    bool CardAcrylic,
    Color CardSolidColor,
    Color CardAcrylicTint,
    double CardAcrylicTintOpacity,
    Color CardFallbackColor,
    Color CardBorderColor,
    Color DimColor,
    Color TitleColor,
    Color CountdownColor,
    Color TipColor,
    Color HintColor,
    Color RingTrackColor,
    Color RingArcColor,
    Color ButtonColor,
    Color ButtonPointerOverColor,
    Color ButtonPressedColor,
    Color ButtonForegroundColor)
{
    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    /// <summary>
    /// Frosted glass (default): Focust-style white acrylic card floating over
    /// the dimmed background, white text tiers and a white elapsed arc.
    /// </summary>
    public static EyeRestOverlayThemePalette Glass { get; } = new(
        Name: EyeRestOverlayThemeFactory.ThemeGlass,
        HasCard: true,
        CardAcrylic: true,
        CardSolidColor: Argb(0x99, 0x14, 0x14, 0x19),
        CardAcrylicTint: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        CardAcrylicTintOpacity: 0.10,
        CardFallbackColor: Argb(0x99, 0x14, 0x14, 0x19),
        CardBorderColor: Argb(0x1A, 0xFF, 0xFF, 0xFF),
        DimColor: Argb(0x59, 0x0F, 0x17, 0x2A),
        TitleColor: Argb(0xE6, 0xFF, 0xFF, 0xFF),
        CountdownColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        TipColor: Argb(0xE6, 0xC8, 0xC8, 0xCC),
        HintColor: Argb(0x80, 0xFF, 0xFF, 0xFF),
        RingTrackColor: Argb(0x26, 0xFF, 0xFF, 0xFF),
        RingArcColor: Argb(0xE6, 0xFF, 0xFF, 0xFF),
        ButtonColor: Argb(0xFF, 0x16, 0xA3, 0x4A),
        ButtonPointerOverColor: Argb(0xFF, 0x15, 0x80, 0x3D),
        ButtonPressedColor: Argb(0xFF, 0x16, 0x65, 0x34),
        ButtonForegroundColor: Argb(0xFF, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// Ink: near-opaque charcoal card with a warm amber arc and button — a
    /// desk-lamp mood with the highest text contrast of the set.
    /// </summary>
    public static EyeRestOverlayThemePalette Ink { get; } = new(
        Name: EyeRestOverlayThemeFactory.ThemeInk,
        HasCard: true,
        CardAcrylic: false,
        CardSolidColor: Argb(0xF2, 0x10, 0x10, 0x14),
        CardAcrylicTint: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        CardAcrylicTintOpacity: 0.10,
        CardFallbackColor: Argb(0xF2, 0x10, 0x10, 0x14),
        CardBorderColor: Argb(0x33, 0xFF, 0xFF, 0xFF),
        DimColor: Argb(0x73, 0x0B, 0x0E, 0x14),
        TitleColor: Argb(0xF5, 0xFF, 0xFF, 0xFF),
        CountdownColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        TipColor: Argb(0xD9, 0xC4, 0xC8, 0xD0),
        HintColor: Argb(0x66, 0xFF, 0xFF, 0xFF),
        RingTrackColor: Argb(0x33, 0xFF, 0xFF, 0xFF),
        RingArcColor: Argb(0xFF, 0xF5, 0x9E, 0x0B),
        ButtonColor: Argb(0xFF, 0xF5, 0x9E, 0x0B),
        ButtonPointerOverColor: Argb(0xFF, 0xD9, 0x77, 0x06),
        ButtonPressedColor: Argb(0xFF, 0xB4, 0x53, 0x09),
        ButtonForegroundColor: Argb(0xFF, 0x11, 0x13, 0x18));

    /// <summary>
    /// Paper: warm light card with dark ink text and a teal arc/button — the
    /// readable daytime look, the only light-surface theme in the set.
    /// </summary>
    public static EyeRestOverlayThemePalette Paper { get; } = new(
        Name: EyeRestOverlayThemeFactory.ThemePaper,
        HasCard: true,
        CardAcrylic: false,
        CardSolidColor: Argb(0xF2, 0xF7, 0xF4, 0xEC),
        CardAcrylicTint: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        CardAcrylicTintOpacity: 0.10,
        CardFallbackColor: Argb(0xF2, 0xF7, 0xF4, 0xEC),
        CardBorderColor: Argb(0x1F, 0x1F, 0x29, 0x37),
        DimColor: Argb(0x59, 0x0F, 0x17, 0x2A),
        TitleColor: Argb(0xE6, 0x1F, 0x29, 0x37),
        CountdownColor: Argb(0xFF, 0x11, 0x18, 0x27),
        TipColor: Argb(0xCC, 0x3F, 0x4A, 0x5A),
        HintColor: Argb(0x99, 0x5B, 0x64, 0x72),
        RingTrackColor: Argb(0x33, 0x1F, 0x29, 0x37),
        RingArcColor: Argb(0xFF, 0x0F, 0x76, 0x6E),
        ButtonColor: Argb(0xFF, 0x0F, 0x76, 0x6E),
        ButtonPointerOverColor: Argb(0xFF, 0x11, 0x5E, 0x59),
        ButtonPressedColor: Argb(0xFF, 0x13, 0x4E, 0x4A),
        ButtonForegroundColor: Argb(0xFF, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// Minimal: no card at all — the content floats on a deeper dim layer with
    /// a quiet translucent resume pill, for people who find any panel heavy.
    /// </summary>
    public static EyeRestOverlayThemePalette Minimal { get; } = new(
        Name: EyeRestOverlayThemeFactory.ThemeMinimal,
        HasCard: false,
        CardAcrylic: false,
        CardSolidColor: Argb(0x00, 0x00, 0x00, 0x00),
        CardAcrylicTint: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        CardAcrylicTintOpacity: 0,
        CardFallbackColor: Argb(0x00, 0x00, 0x00, 0x00),
        CardBorderColor: Argb(0x00, 0x00, 0x00, 0x00),
        DimColor: Argb(0x8C, 0x0B, 0x0E, 0x14),
        TitleColor: Argb(0xF5, 0xFF, 0xFF, 0xFF),
        CountdownColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
        TipColor: Argb(0xD9, 0xD3, 0xD7, 0xDD),
        HintColor: Argb(0x66, 0xFF, 0xFF, 0xFF),
        RingTrackColor: Argb(0x1F, 0xFF, 0xFF, 0xFF),
        RingArcColor: Argb(0xE6, 0xFF, 0xFF, 0xFF),
        ButtonColor: Argb(0x26, 0xFF, 0xFF, 0xFF),
        ButtonPointerOverColor: Argb(0x33, 0xFF, 0xFF, 0xFF),
        ButtonPressedColor: Argb(0x40, 0xFF, 0xFF, 0xFF),
        ButtonForegroundColor: Argb(0xFF, 0xFF, 0xFF, 0xFF));
}

/// <summary>
/// Wire-name registry for the overlay themes. Unknown or stale values fall
/// back to Glass at every consumption point, mirroring
/// <c>EyeRestBreakBackgroundFactory.NormalizeMode</c>.
/// </summary>
public static class EyeRestOverlayThemeFactory
{
    public const string ThemeGlass = "Glass";
    public const string ThemeInk = "Ink";
    public const string ThemePaper = "Paper";
    public const string ThemeMinimal = "Minimal";

    /// <summary>Combo order for the settings section.</summary>
    public static readonly string[] AllThemes = [ThemeGlass, ThemeInk, ThemePaper, ThemeMinimal];

    public static string NormalizeTheme(string? theme) => theme switch
    {
        ThemeInk => ThemeInk,
        ThemePaper => ThemePaper,
        ThemeMinimal => ThemeMinimal,
        _ => ThemeGlass
    };

    public static EyeRestOverlayThemePalette Resolve(string? theme) => NormalizeTheme(theme) switch
    {
        ThemeInk => EyeRestOverlayThemePalette.Ink,
        ThemePaper => EyeRestOverlayThemePalette.Paper,
        ThemeMinimal => EyeRestOverlayThemePalette.Minimal,
        _ => EyeRestOverlayThemePalette.Glass
    };
}
