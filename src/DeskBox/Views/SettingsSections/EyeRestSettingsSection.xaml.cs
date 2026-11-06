using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// The eye-rest break settings page: master enablement, the four cadence
/// numbers, and the overlay background preferences (mode, color, image,
/// folder). Pure code-behind (DisplaySettingsSection precedent): every write
/// goes through <see cref="IEyeRestSettings"/>, whose coordinator persists,
/// clamps, and reconciles the live timer session — this section never touches
/// settings storage or the runtime directly.
/// </summary>
public sealed partial class EyeRestSettingsSection : UserControl
{
    private static readonly IReadOnlyList<string> BackgroundImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    // ComboBoxItem subclass (Glance's SelectionItem precedent): Content holds
    // the localized label, no reflection-based DisplayMemberPath under AOT.
    private sealed partial class BackgroundModeItem : ComboBoxItem
    {
        public BackgroundModeItem(string label, string wireValue)
        {
            Content = label;
            WireValue = wireValue;
        }

        public string WireValue { get; }
    }

    private IEyeRestSettings _settings = null!;
    private IntPtr _ownerWindow;
    private bool _isRefreshing;
    private string _backgroundModesLocalizationSignature = string.Empty;

    private LocalizationService Localization => App.Current.LocalizationService;

    public EyeRestSettingsSection()
    {
        InitializeComponent();
        Loaded += OnSectionLoaded;
    }

    /// <summary>
    /// Injected by the settings window when the deferred section is created,
    /// before the first Loaded. The owner window handle backs the background
    /// image/folder pickers.
    /// </summary>
    public void Configure(IEyeRestSettings settings, IntPtr ownerWindow)
    {
        _settings = settings;
        _ownerWindow = ownerWindow;
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

            // Raw preferences (not the resolved background): the combo must
            // stay on a Folder mode whose folder is empty, and the path labels
            // show the persisted paths even when the mode is currently Solid.
            EyeRestBackgroundPreferences background = _settings.ReadBackgroundPreferences();
            PopulateBackgroundModes();
            SelectBackgroundMode(background.Mode);
            Color color = ParseColor(background.ColorHex);
            BackgroundColorSwatch.Background = new SolidColorBrush(color);
            BackgroundColorPicker.Color = color;
            BackgroundImagePathText.Text = background.ImagePath;
            BackgroundFolderPathText.Text = background.FolderPath;
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

    private void BackgroundModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || _settings is null)
        {
            return;
        }

        string mode = ReadSelectedBackgroundMode();
        try
        {
            _settings.SetBackgroundMode(mode);
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings background-mode write failed: {ex}");
        }

        UpdateBackgroundModeCards();
    }

    private void BackgroundColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isRefreshing || _settings is null)
        {
            return;
        }

        try
        {
            _settings.SetBackgroundColor(ToHex(args.NewColor));
            BackgroundColorSwatch.Background = new SolidColorBrush(args.NewColor);
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings background-color write failed: {ex}");
        }
    }

    private async void ChooseBackgroundImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _ownerWindow == IntPtr.Zero)
        {
            return;
        }

        string? path = await FileOpenPickerService.PickSingleFileAsync(
            _ownerWindow,
            BackgroundImageExtensions,
            Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // Picking an image also switches the mode (Glance folder-pick
            // precedent): a picked path that leaves the mode on Solid would
            // look like the picker silently did nothing.
            _settings.SetBackgroundImage(path);
            _settings.SetBackgroundMode(EyeRestBreakBackgroundFactory.ModeImage);
            BackgroundImagePathText.Text = path;
            SelectBackgroundMode(EyeRestBreakBackgroundFactory.ModeImage);
            UpdateBackgroundModeCards();
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings background-image write failed: {ex}");
        }
    }

    private async void ChooseBackgroundFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _ownerWindow == IntPtr.Zero)
        {
            return;
        }

        string? folder = await FolderPickerService.PickFolderAsync(_ownerWindow);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            _settings.SetBackgroundFolder(folder);
            _settings.SetBackgroundMode(EyeRestBreakBackgroundFactory.ModeFolder);
            BackgroundFolderPathText.Text = folder;
            SelectBackgroundMode(EyeRestBreakBackgroundFactory.ModeFolder);
            UpdateBackgroundModeCards();
        }
        catch (Exception ex)
        {
            App.Log($"[EyeRest] Settings background-folder write failed: {ex}");
        }
    }

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
        BackgroundModeCard.IsEnabled = enabled;
        UpdateBackgroundModeCards();
    }

    /// <summary>Only the card matching the selected mode is actionable; the others are inert.</summary>
    private void UpdateBackgroundModeCards()
    {
        string mode = ReadSelectedBackgroundMode();
        bool enabled = EnableToggle.IsOn;
        BackgroundColorCard.IsEnabled = enabled && mode == EyeRestBreakBackgroundFactory.ModeSolid;
        BackgroundImageCard.IsEnabled = enabled && mode == EyeRestBreakBackgroundFactory.ModeImage;
        BackgroundFolderCard.IsEnabled = enabled && mode == EyeRestBreakBackgroundFactory.ModeFolder;
    }

    private void PopulateBackgroundModes()
    {
        // Glance's options-signature pattern: rebuild the localized labels when
        // the effective culture changed, keep the selection.
        string signature = Localization.CurrentCultureName;
        if (BackgroundModeComboBox.Items.Count > 0 &&
            string.Equals(_backgroundModesLocalizationSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        string selected = ReadSelectedBackgroundMode();
        BackgroundModeComboBox.Items.Clear();
        BackgroundModeComboBox.Items.Add(new BackgroundModeItem(
            Localization.T("Settings.EyeRest.Background.Mode.Solid"),
            EyeRestBreakBackgroundFactory.ModeSolid));
        BackgroundModeComboBox.Items.Add(new BackgroundModeItem(
            Localization.T("Settings.EyeRest.Background.Mode.Image"),
            EyeRestBreakBackgroundFactory.ModeImage));
        BackgroundModeComboBox.Items.Add(new BackgroundModeItem(
            Localization.T("Settings.EyeRest.Background.Mode.Folder"),
            EyeRestBreakBackgroundFactory.ModeFolder));
        _backgroundModesLocalizationSignature = signature;
        SelectBackgroundMode(selected);
    }

    private void SelectBackgroundMode(string wireValue)
    {
        BackgroundModeComboBox.SelectedItem = BackgroundModeComboBox.Items
            .OfType<BackgroundModeItem>()
            .FirstOrDefault(item => item.WireValue == wireValue);
        if (BackgroundModeComboBox.SelectedItem is null && BackgroundModeComboBox.Items.Count > 0)
        {
            BackgroundModeComboBox.SelectedIndex = 0;
        }
    }

    private string ReadSelectedBackgroundMode() =>
        BackgroundModeComboBox.SelectedItem is BackgroundModeItem { WireValue: { } wire }
            ? wire
            : EyeRestBreakBackgroundFactory.ModeSolid;

    private static Color ParseColor(string hex)
    {
        string normalized = EyeRestBreakBackgroundFactory.NormalizeColorHex(hex);
        return Color.FromArgb(
            0xFF,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static int ToInt(NumberBoxValueChangedEventArgs args) =>
        (int)Math.Round(double.IsFinite(args.NewValue) ? args.NewValue : 0);
}
