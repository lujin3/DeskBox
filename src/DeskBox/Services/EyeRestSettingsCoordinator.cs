using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using DeskBox.Models;
using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Windows.UI;

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

    public EyeRestBreakBackground ReadBreakBackground()
    {
        EyeRestBackgroundPreferences preferences = ReadBackgroundPreferences();
        return EyeRestBreakBackgroundFactory.Resolve(
            preferences.Mode,
            preferences.ColorHex,
            preferences.ImagePath,
            preferences.FolderPath,
            message => App.Log(message));
    }

    public EyeRestBackgroundPreferences ReadBackgroundPreferences()
    {
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        return new EyeRestBackgroundPreferences(
            EyeRestBreakBackgroundFactory.NormalizeMode(eyeRest.EyeRestBackgroundMode),
            EyeRestBreakBackgroundFactory.NormalizeColorHex(eyeRest.EyeRestBackgroundColor),
            eyeRest.EyeRestBackgroundImage,
            eyeRest.EyeRestBackgroundFolder);
    }

    public void SetBackgroundMode(string mode) =>
        SetStringPreference(
            eyeRest => eyeRest.EyeRestBackgroundMode,
            (eyeRest, value) => eyeRest.EyeRestBackgroundMode = value,
            EyeRestBreakBackgroundFactory.NormalizeMode(mode));

    public void SetBackgroundColor(string colorHex) =>
        SetStringPreference(
            eyeRest => eyeRest.EyeRestBackgroundColor,
            (eyeRest, value) => eyeRest.EyeRestBackgroundColor = value,
            EyeRestBreakBackgroundFactory.NormalizeColorHex(colorHex));

    public void SetBackgroundImage(string path) =>
        SetStringPreference(
            eyeRest => eyeRest.EyeRestBackgroundImage,
            (eyeRest, value) => eyeRest.EyeRestBackgroundImage = value,
            path ?? string.Empty);

    public void SetBackgroundFolder(string path) =>
        SetStringPreference(
            eyeRest => eyeRest.EyeRestBackgroundFolder,
            (eyeRest, value) => eyeRest.EyeRestBackgroundFolder = value,
            path ?? string.Empty);

    /// <summary>
    /// One break's resolved overlay surface. The window paints the raw
    /// background under Focust's scrim with its frosted-glass card as fixed
    /// presentation (PromptApp.vue); this surface carries only what settings
    /// actually influence — the effective theme (for the templated button
    /// states) and the Focust tier colors: slate-950/35 scrim, white text
    /// tiers (the scrim is dark in both themes, so dark ink text would sink
    /// into it), the progress arc in the same white family Focust uses.
    /// </summary>
    public EyeRestOverlaySurface ReadOverlaySurface()
    {
        // EffectiveTheme already resolves a "System" preference through the
        // OS; the Win32 fallback only covers a ThemeService-less context
        // (never at break time, but the coordinator stays unit-callable).
        ThemeService? themeService = App.Current.ThemeService;
        bool isDark = themeService is null
            ? Win32Helper.IsSystemDarkMode()
            : themeService.EffectiveTheme == ElementTheme.Dark;

        return new EyeRestOverlaySurface(
            IsDark: isDark,
            DimColor: Argb(0x59, 0x02, 0x06, 0x17),
            TitleColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
            CountdownColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
            TipColor: Argb(0xE6, 0xFF, 0xFF, 0xFF),
            HintColor: Argb(0x80, 0xFF, 0xFF, 0xFF),
            RingTrackColor: Argb(0x26, 0xFF, 0xFF, 0xFF),
            RingArcColor: Argb(0xE6, 0xFF, 0xFF, 0xFF));
    }

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public void ResetPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        eyeRest.EyeRestEnabled = false;
        eyeRest.EyeRestWorkIntervalMinutes = 20;
        eyeRest.EyeRestMiniBreakSeconds = 20;
        eyeRest.EyeRestLongBreakMinutes = 5;
        eyeRest.EyeRestLongBreakEveryMiniBreaks = 4;
        eyeRest.EyeRestBackgroundMode = EyeRestBreakBackgroundFactory.ModeSolid;
        eyeRest.EyeRestBackgroundColor = EyeRestBreakBackground.DefaultColorHex;
        eyeRest.EyeRestBackgroundImage = string.Empty;
        eyeRest.EyeRestBackgroundFolder = string.Empty;
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

    /// <summary>
    /// String preference twin of <see cref="SetPreference"/>: the raw value is
    /// already normalized by the factory (mode/color) or accepted verbatim
    /// (paths); equal current values are no-ops. Background preferences never
    /// reach the runtime reconcile inputs — they only repaint the next break's
    /// overlay — so no timer churn on writes.
    /// </summary>
    private void SetStringPreference(
        Func<EyeRestSettingsSlice, string> readValue,
        Action<EyeRestSettingsSlice, string> apply,
        string normalizedValue)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        EyeRestSettingsSlice eyeRest = _settings.Settings.EyeRest;
        if (string.Equals(readValue(eyeRest), normalizedValue, StringComparison.Ordinal))
        {
            return;
        }

        apply(eyeRest, normalizedValue);
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
