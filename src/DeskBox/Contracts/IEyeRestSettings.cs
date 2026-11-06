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

    /// <summary>Restores every preference to its default.</summary>
    void ResetPreferences(bool scheduleSave = true);

    /// <summary>Stops reacting to settings changes (shutdown).</summary>
    void Stop();
}
