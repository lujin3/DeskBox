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
}
