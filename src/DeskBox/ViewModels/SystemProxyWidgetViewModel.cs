using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// View model for the system-proxy widget. The Windows WinINet manual-proxy
/// state is the single source of truth — the view model only mirrors it,
/// resyncing on reveal/activation and whenever the in-process
/// <see cref="SystemProxyService"/> flips the switch (card or tray).
/// </summary>
public sealed partial class SystemProxyWidgetViewModel : ObservableObject, IDisposable
{
    private readonly WidgetConfig _config;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _isDisposed;
    private bool _isProxyEnabled;
    private string _serverText = string.Empty;
    private string _serverLabel = string.Empty;
    private string _notConfiguredText = string.Empty;
    private string _headerTitle = string.Empty;

    public SystemProxyWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        DispatcherQueue? dispatcherQueue = null)
    {
        _config = config;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();

        _localizationService.LanguageChanged += OnLanguageChanged;
        SystemProxyService.StateChanged += OnSystemProxyStateChanged;

        ApplyLocalizedTexts();
        RefreshFromSystem();
    }

    private static DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            return null;
        }
    }

    // ─── Observable Properties ─────────────────────────────────

    public string HeaderTitle
    {
        get => _headerTitle;
        private set => SetProperty(ref _headerTitle, value);
    }

    /// <summary>Mirrors the live WinINet ProxyEnable flag.</summary>
    public bool IsProxyEnabled
    {
        get => _isProxyEnabled;
        private set => SetProperty(ref _isProxyEnabled, value);
    }

    public string ServerLabel
    {
        get => _serverLabel;
        private set => SetProperty(ref _serverLabel, value);
    }

    public string ServerText
    {
        get => _serverText;
        private set => SetProperty(ref _serverText, value);
    }

    public string NotConfiguredText
    {
        get => _notConfiguredText;
        private set => SetProperty(ref _notConfiguredText, value);
    }

    // Computed for the XAML Binding surface (Visibility mirrors the
    // server-configured state; one of the two lines is always shown).
    public Microsoft.UI.Xaml.Visibility ServerRowVisibility =>
        string.IsNullOrWhiteSpace(_serverText)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility NotConfiguredVisibility =>
        string.IsNullOrWhiteSpace(_serverText)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    // ─── Toggle entry point ────────────────────────────────────

    /// <summary>
    /// Applies a user toggle. On refusal (no server configured) or failure the
    /// mirror snaps back to the real system state, which visually reverts the
    /// switch.
    /// </summary>
    public Task SetProxyEnabledAsync(bool enable)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        bool applied = SystemProxyService.TrySetEnabled(enable);
        if (!applied)
        {
            // StateChanged never fired — pull the authoritative value so the
            // switch UI falls back to reality instead of the wish.
            RefreshFromSystem();
        }

        return Task.CompletedTask;
    }

    // ─── Lifecycle ─────────────────────────────────────────────

    public Task InitializeAsync()
    {
        RefreshFromSystem();
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        RefreshFromSystem();
        return Task.CompletedTask;
    }

    public void OnActivated()
    {
        if (!_isDisposed)
        {
            RefreshFromSystem();
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (!_isDisposed)
        {
            RefreshFromSystem();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        SystemProxyService.StateChanged -= OnSystemProxyStateChanged;
    }

    // ─── Private ───────────────────────────────────────────────

    private void RefreshFromSystem()
    {
        var state = SystemProxyService.Read();
        IsProxyEnabled = state.Enabled;
        ServerText = state.HasServer ? state.Server : string.Empty;
        OnPropertyChanged(nameof(ServerRowVisibility));
        OnPropertyChanged(nameof(NotConfiguredVisibility));
    }

    private void ApplyLocalizedTexts()
    {
        HeaderTitle = _config.IsDefaultTitle
            ? _localizationService.T("SystemProxy.Title")
            : _config.Name;
        ServerLabel = _localizationService.T("SystemProxy.ServerLabel");
        NotConfiguredText = _localizationService.T("SystemProxy.NotConfigured");
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedTexts();
    }

    /// <summary>
    /// Tray and card share the service, so a tray toggle must resync the card.
    /// The event fires on the writing thread; hop to the UI dispatcher before
    /// touching observable state (the write path is normally the UI thread
    /// already, but the tray menu hides and awaits first, so do not rely on it).
    /// </summary>
    private void OnSystemProxyStateChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshFromSystem);
            return;
        }

        RefreshFromSystem();
    }
}
