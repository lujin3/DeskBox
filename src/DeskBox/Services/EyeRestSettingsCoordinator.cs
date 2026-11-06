using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using DeskBox.Helpers;
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
    /// One break's resolved overlay surface. The card reuses the widget
    /// material design language — the same visual calculator inputs the
    /// widget windows and the Glance calendar panel consume (material type,
    /// surface opacity, material intensity, effective theme, effective
    /// accent) plus the widget border calculator — so the break overlay
    /// reads as a floating widget surface, never a divergent theme.
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
        Color accent = themeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        AppSettings settings = _settings.Settings;
        double surfaceOpacity = Math.Clamp(settings.WidgetOpacity, 0.0, 1.0);
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            settings.WidgetMaterialType);

        EyeRestOverlayCardMaterial cardMaterial = EyeRestOverlayCardMaterial.Tinted;
        Color cardColor;
        double cardTintOpacity = 0;
        double cardLuminosityOpacity = 0;
        if (SettingsService.IsAcrylicMaterial(materialType))
        {
            // Glance calendar-panel precedent: in-app XAML acrylic with the
            // same tint/luminosity profile the widget surfaces use.
            cardMaterial = EyeRestOverlayCardMaterial.Acrylic;
            cardColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accent);
            WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
                isDark,
                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                surfaceOpacity,
                settings.WidgetMaterialIntensity);
            cardTintOpacity = profile.TintOpacity;
            cardLuminosityOpacity = profile.LuminosityOpacity;
        }
        else if (SettingsService.IsMicaMaterial(materialType))
        {
            // Mica is a window-level material; the app's in-window surfaces
            // approximate it with the embedded tint overlay instead.
            cardColor = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
                isDark,
                accent,
                materialType == SettingsService.WidgetMaterialTypeMicaAlt,
                settings.WidgetMaterialIntensity);
        }
        else
        {
            cardColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accent,
                surfaceOpacity);
        }

        WidgetBorderVisuals border = WidgetBorderVisualCalculator.Resolve(
            settings.WidgetBorderStyle,
            settings.WidgetBorderColorMode,
            isDark,
            accent);
        Color cardFallbackColor = Color.FromArgb(0xFF, cardColor.R, cardColor.G, cardColor.B);

        // Text tiers, dim strength and ring track follow the effective theme;
        // the elapsed arc carries the effective accent, tying the countdown
        // to the app's accent design language. The dim stays dark in both
        // themes — the rest screen should emit less light, not more.
        return isDark
            ? new EyeRestOverlaySurface(
                IsDark: true,
                CardMaterial: cardMaterial,
                CardTintColor: cardColor,
                CardTintOpacity: cardTintOpacity,
                CardLuminosityOpacity: cardLuminosityOpacity,
                CardFallbackColor: cardFallbackColor,
                CardBorderThickness: border.Thickness,
                CardBorderColor: border.BorderColor,
                DimColor: Argb(0x73, 0x0B, 0x0E, 0x14),
                TitleColor: Argb(0xE6, 0xFF, 0xFF, 0xFF),
                CountdownColor: Argb(0xFF, 0xFF, 0xFF, 0xFF),
                TipColor: Argb(0xE6, 0xC8, 0xC8, 0xCC),
                HintColor: Argb(0x80, 0xFF, 0xFF, 0xFF),
                RingTrackColor: Argb(0x26, 0xFF, 0xFF, 0xFF),
                RingArcColor: accent)
            : new EyeRestOverlaySurface(
                IsDark: false,
                CardMaterial: cardMaterial,
                CardTintColor: cardColor,
                CardTintOpacity: cardTintOpacity,
                CardLuminosityOpacity: cardLuminosityOpacity,
                CardFallbackColor: cardFallbackColor,
                CardBorderThickness: border.Thickness,
                CardBorderColor: border.BorderColor,
                DimColor: Argb(0x59, 0x0F, 0x17, 0x2A),
                TitleColor: Argb(0xE6, 0x1F, 0x29, 0x37),
                CountdownColor: Argb(0xFF, 0x11, 0x18, 0x27),
                TipColor: Argb(0xCC, 0x3F, 0x4A, 0x5A),
                HintColor: Argb(0x99, 0x5B, 0x64, 0x72),
                RingTrackColor: Argb(0x33, 0x1F, 0x29, 0x37),
                RingArcColor: accent);
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
