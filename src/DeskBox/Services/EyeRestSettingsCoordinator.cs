using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Single writer for the eye-rest break preferences, modeled on
/// <see cref="TodoSettingsCoordinator"/>: setter paths mutate the slice,
/// schedule a debounced save, and reconcile the runtime directly; the
/// <c>SettingsChanged</c> path reconciles only when an eye-rest-relevant
/// input actually changed (the event fires for every debounced save of every
/// slice). Invalid values never reach the runtime — they are clamped by
/// <see cref="EyeRestBreakScheduleFactory"/> on the way into
/// <see cref="EyeRestRuntimeSettings"/>.
/// </summary>
public sealed class EyeRestSettingsCoordinator : IEyeRestSettings
{
    private readonly SettingsService _settings;
    private readonly Action<EyeRestRuntimeSettings> _refreshRuntime;
    private (bool Enabled, int Work, int Mini, int Long, int Every)? _lastReconciledInputs;
    private bool _updatingPreferences;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public EyeRestSettingsCoordinator(
        SettingsService settings,
        Action<EyeRestRuntimeSettings> refreshRuntime)
    {
        _settings = settings;
        _refreshRuntime = refreshRuntime;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public EyeRestRuntimeSettings Read()
    {
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        return new EyeRestRuntimeSettings(
            eyeRest.EyeRestEnabled,
            EyeRestBreakScheduleFactory.Create(
                eyeRest.EyeRestWorkIntervalMinutes,
                eyeRest.EyeRestMiniBreakSeconds,
                eyeRest.EyeRestLongBreakMinutes,
                eyeRest.EyeRestLongBreakEveryMiniBreaks));
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings.Settings.EyeRest.EyeRestEnabled == enabled)
        {
            return;
        }

        _settings.Settings.EyeRest.EyeRestEnabled = enabled;
        _updatingPreferences = true;
        try
        {
            // SaveAsync(notifySubscribers: true) fires SettingsChanged on
            // completion; the flag above keeps our own handler from double
            // reconciling — the direct call below is the reconcile.
            await _settings.SaveAsync();
        }
        finally
        {
            _updatingPreferences = false;
        }

        Reconcile(Read());
    }

    public void SetWorkIntervalMinutes(int minutes) =>
        SetPreference(
            eyeRest => eyeRest.EyeRestWorkIntervalMinutes,
            (eyeRest, value) => eyeRest.EyeRestWorkIntervalMinutes = value,
            minutes,
            EyeRestBreakScheduleFactory.MinWorkIntervalMinutes,
            EyeRestBreakScheduleFactory.MaxWorkIntervalMinutes);

    public void SetMiniBreakSeconds(int seconds) =>
        SetPreference(
            eyeRest => eyeRest.EyeRestMiniBreakSeconds,
            (eyeRest, value) => eyeRest.EyeRestMiniBreakSeconds = value,
            seconds,
            EyeRestBreakScheduleFactory.MinMiniBreakSeconds,
            EyeRestBreakScheduleFactory.MaxMiniBreakSeconds);

    public void SetLongBreakMinutes(int minutes) =>
        SetPreference(
            eyeRest => eyeRest.EyeRestLongBreakMinutes,
            (eyeRest, value) => eyeRest.EyeRestLongBreakMinutes = value,
            minutes,
            EyeRestBreakScheduleFactory.MinLongBreakMinutes,
            EyeRestBreakScheduleFactory.MaxLongBreakMinutes);

    public void SetLongBreakEveryMiniBreaks(int count) =>
        SetPreference(
            eyeRest => eyeRest.EyeRestLongBreakEveryMiniBreaks,
            (eyeRest, value) => eyeRest.EyeRestLongBreakEveryMiniBreaks = value,
            count,
            EyeRestBreakScheduleFactory.MinLongBreakEveryMiniBreaks,
            EyeRestBreakScheduleFactory.MaxLongBreakEveryMiniBreaks);

    public void ResetPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        eyeRest.EyeRestEnabled = false;
        eyeRest.EyeRestWorkIntervalMinutes = 20;
        eyeRest.EyeRestMiniBreakSeconds = 20;
        eyeRest.EyeRestLongBreakMinutes = 5;
        eyeRest.EyeRestLongBreakEveryMiniBreaks = 4;
        if (scheduleSave)
        {
            _updatingPreferences = true;
            try
            {
                _settings.SaveDebounced();
            }
            finally
            {
                _updatingPreferences = false;
            }
        }

        Reconcile(Read());
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    private void SetPreference(
        Func<EyeRestSettingsSlice, int> readValue,
        Action<EyeRestSettingsSlice, int> apply,
        int rawValue,
        int min,
        int max)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        int normalized = Math.Clamp(rawValue, min, max);
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        if (readValue(eyeRest) == normalized)
        {
            return;
        }

        apply(eyeRest, normalized);
        _updatingPreferences = true;
        try
        {
            _settings.SaveDebounced();
        }
        finally
        {
            _updatingPreferences = false;
        }

        Reconcile(Read());
    }

    private void OnSettingsChanged()
    {
        if (_stopped || _updatingPreferences)
        {
            return;
        }

        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        var inputs = (eyeRest.EyeRestEnabled,
            eyeRest.EyeRestWorkIntervalMinutes,
            eyeRest.EyeRestMiniBreakSeconds,
            eyeRest.EyeRestLongBreakMinutes,
            eyeRest.EyeRestLongBreakEveryMiniBreaks);
        if (_lastReconciledInputs == inputs)
        {
            return;
        }

        Reconcile(Read());
    }

    private void Reconcile(EyeRestRuntimeSettings settings)
    {
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        _lastReconciledInputs = (eyeRest.EyeRestEnabled,
            eyeRest.EyeRestWorkIntervalMinutes,
            eyeRest.EyeRestMiniBreakSeconds,
            eyeRest.EyeRestLongBreakMinutes,
            eyeRest.EyeRestLongBreakEveryMiniBreaks);
        _refreshRuntime(settings);
    }
}
