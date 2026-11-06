using DeskBox.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// The eye-rest break settings page: master enablement plus the four cadence
/// numbers. Pure code-behind (DisplaySettingsSection precedent): every write
/// goes through <see cref="IEyeRestSettings"/>, whose coordinator persists,
/// clamps, and reconciles the live timer session — this section never touches
/// settings storage or the runtime directly.
/// </summary>
public sealed partial class EyeRestSettingsSection : UserControl
{
    private IEyeRestSettings _settings = null!;
    private bool _isRefreshing;

    public EyeRestSettingsSection()
    {
        InitializeComponent();
        Loaded += OnSectionLoaded;
    }

    /// <summary>
    /// Injected by the settings window when the deferred section is created,
    /// before the first Loaded.
    /// </summary>
    public void Configure(IEyeRestSettings settings)
    {
        _settings = settings;
        Refresh();
    }

    /// <summary>Re-reads every control from the coordinator (nav entry / external change).</summary>
    internal void Refresh()
    {
        if (_settings is null)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            EyeRestRuntimeSettings current = _settings.Read();
            EnableToggle.IsOn = current.Enabled;
            WorkIntervalBox.Value = current.Schedule.WorkInterval.TotalMinutes;
            MiniBreakBox.Value = current.Schedule.MiniBreakDuration.TotalSeconds;
            LongBreakBox.Value = current.Schedule.LongBreakDuration.TotalMinutes;
            LongEveryBox.Value = current.Schedule.LongBreakEveryMiniBreaks;
            SetCadenceEnabled(current.Enabled);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnSectionLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        bool enabled = EnableToggle.IsOn;
        SetCadenceEnabled(enabled);
        _ = RunEnabledWriteAsync(enabled);
    }

    private async System.Threading.Tasks.Task RunEnabledWriteAsync(bool enabled)
    {
        try
        {
            await _settings.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings enable write failed: {ex}");
        }
    }

    private void WorkIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        WriteCadence(() => _settings.SetWorkIntervalMinutes(ToInt(args)));

    private void MiniBreakBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        WriteCadence(() => _settings.SetMiniBreakSeconds(ToInt(args)));

    private void LongBreakBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        WriteCadence(() => _settings.SetLongBreakMinutes(ToInt(args)));

    private void LongEveryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        WriteCadence(() => _settings.SetLongBreakEveryMiniBreaks(ToInt(args)));

    private void WriteCadence(System.Action write)
    {
        if (_isRefreshing)
        {
            return;
        }

        try
        {
            write();
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings cadence write failed: {ex}");
        }
    }

    private void SetCadenceEnabled(bool enabled)
    {
        WorkIntervalBox.IsEnabled = enabled;
        MiniBreakBox.IsEnabled = enabled;
        LongBreakBox.IsEnabled = enabled;
        LongEveryBox.IsEnabled = enabled;
    }

    private static int ToInt(NumberBoxValueChangedEventArgs args) =>
        (int)Math.Round(double.IsFinite(args.NewValue) ? args.NewValue : 0);
}
