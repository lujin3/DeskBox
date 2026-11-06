using DeskBox.Features.EyeRest;

namespace DeskBox.Contracts;

/// <summary>
/// Effective eye-rest runtime inputs: enablement plus the clamped break
/// schedule. The schedule is built through
/// <see cref="EyeRestBreakScheduleFactory"/> so every consumer sees the same
/// clamped values.
/// </summary>
public readonly record struct EyeRestRuntimeSettings(bool Enabled, EyeRestBreakSchedule Schedule)
{
    public bool ShouldRun => Enabled;
}

/// <summary>
/// Raw, unresolved break background preferences: the settings UI displays
/// these verbatim (a Folder mode with an empty folder must keep showing
/// "Folder"), while <see cref="IEyeRestSettings.ReadBreakBackground"/> hands
/// the runtime the resolved per-break background.
/// </summary>
public sealed record EyeRestBackgroundPreferences(
    string Mode,
    string ColorHex,
    string ImagePath,
    string FolderPath);

/// <summary>
/// One writer for the eye-rest break preferences (14th settings slice).
/// Preference writes schedule a debounced save and reconcile the runtime
/// directly; external settings changes (second settings host, hand-edited
/// settings.json) arrive through <c>SettingsChanged</c> and reconcile only
/// when an eye-rest-relevant input actually changed.
/// </summary>
public interface IEyeRestSettings
{
    /// <summary>Current enablement and clamped schedule.</summary>
    EyeRestRuntimeSettings Read();

    /// <summary>Raw background preferences for settings UI display.</summary>
    EyeRestBackgroundPreferences ReadBackgroundPreferences();

    /// <summary>Master enablement. Persisted before the runtime is reconciled.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Work interval in minutes. Clamped to 1–180.</summary>
    void SetWorkIntervalMinutes(int minutes);

    /// <summary>Mini break duration in seconds. Clamped to 5–300.</summary>
    void SetMiniBreakSeconds(int seconds);

    /// <summary>Long break duration in minutes. Clamped to 1–60.</summary>
    void SetLongBreakMinutes(int minutes);

    /// <summary>Mini breaks before a long break. Clamped to 1–12.</summary>
    void SetLongBreakEveryMiniBreaks(int count);

    /// <summary>
    /// One break's resolved overlay background: solid color or image path
    /// (a Folder mode picks a random image per call).
    /// </summary>
    EyeRestBreakBackground ReadBreakBackground();

    /// <summary>Overlay background mode on the wire: "Solid", "Image" or "Folder". Unknown values normalize to "Solid".</summary>
    void SetBackgroundMode(string mode);

    /// <summary>Solid-mode overlay color. Invalid hex normalizes to the built-in default.</summary>
    void SetBackgroundColor(string colorHex);

    /// <summary>Absolute path of the single background image (Image mode).</summary>
    void SetBackgroundImage(string path);

    /// <summary>Folder scanned for a random background image per break (Folder mode).</summary>
    void SetBackgroundFolder(string path);

    /// <summary>
    /// One break's resolved overlay surface: the card painted in the app
    /// window material design language (global material type, opacity,
    /// intensity and border settings, plus the effective light/dark theme and
    /// accent), so the overlay never diverges from the widget surfaces.
    /// </summary>
    EyeRestOverlaySurface ReadOverlaySurface();

    /// <summary>Restores every preference to its default.</summary>
    void ResetPreferences(bool scheduleSave = true);

    /// <summary>Stops reacting to settings changes (shutdown).</summary>
    void Stop();
}
