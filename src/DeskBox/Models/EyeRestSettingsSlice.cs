namespace DeskBox.Models;

/// <summary>
/// Eye-rest break preferences: enablement and the two-tier cadence
/// (mini breaks on a work interval, a long break after every N mini breaks).
/// Raw values in minutes/seconds; clamped at consumption by
/// <c>EyeRestBreakScheduleFactory</c>.
/// </summary>
public sealed class EyeRestSettingsSlice
{
    /// <summary>Whether scheduled eye-rest breaks run while DeskBox is running. Off by default: a screen-covering feature is opt-in.</summary>
    public bool EyeRestEnabled { get; set; }

    /// <summary>Minutes of work before a mini break. Clamped to 1–180.</summary>
    public int EyeRestWorkIntervalMinutes { get; set; } = 20;

    /// <summary>Mini break duration in seconds. Clamped to 5–300.</summary>
    public int EyeRestMiniBreakSeconds { get; set; } = 20;

    /// <summary>Long break duration in minutes. Clamped to 1–60.</summary>
    public int EyeRestLongBreakMinutes { get; set; } = 5;

    /// <summary>Mini breaks (started, skip-inclusive) before a long break. Clamped to 1–12.</summary>
    public int EyeRestLongBreakEveryMiniBreaks { get; set; } = 4;

    /// <summary>
    /// Break overlay background mode on the wire: "Solid", "Image" or "Folder"
    /// (random image per break). Unknown values fall back to Solid at
    /// consumption by <c>EyeRestBreakBackgroundFactory</c>.
    /// </summary>
    public string EyeRestBackgroundMode { get; set; } = "Solid";

    /// <summary>
    /// Solid-mode overlay color as #RRGGBB. Invalid hex falls back to the
    /// built-in dark overlay color at consumption.
    /// </summary>
    public string EyeRestBackgroundColor { get; set; } = "#161618";

    /// <summary>Absolute path of the single image used when the mode is "Image".</summary>
    public string EyeRestBackgroundImage { get; set; } = "";

    /// <summary>Folder scanned for a random image per break when the mode is "Folder".</summary>
    public string EyeRestBackgroundFolder { get; set; } = "";

    /// <summary>
    /// Break overlay visual theme on the wire: "Glass", "Ink", "Paper" or
    /// "Minimal". Unknown values fall back to Glass at consumption by
    /// <c>EyeRestOverlayThemeFactory</c>.
    /// </summary>
    public string EyeRestOverlayTheme { get; set; } = "Glass";
}
