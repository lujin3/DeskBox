using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Adapter that hosts the system-proxy card in a content widget window,
/// forwarding the host lifecycle to the view model. The proxy state itself
/// lives in Windows (WinINet), so there is no refresh timer to manage —
/// reveal/activation pulls and the service's StateChanged event push.
/// </summary>
public sealed class SystemProxyWidgetContentAdapter : WidgetContentAdapterBase
{
    public SystemProxyWidgetViewModel ViewModel { get; }

    public SystemProxyWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        Func<SystemProxyWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new SystemProxyWidgetViewModel(config, localizationService),
            viewFactory)
    {
    }

    private SystemProxyWidgetContentAdapter(
        WidgetConfig config,
        SystemProxyWidgetViewModel viewModel,
        Func<SystemProxyWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new SystemProxyWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.SystemProxy)
        {
            throw new ArgumentException(
                "System proxy content requires a SystemProxy widget config.",
                nameof(config));
        }

        ViewModel = viewModel;
    }

    public override Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public override Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public override void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public override void OnWindowRevealCompleted()
    {
        ViewModel.OnWindowRevealCompleted();
    }

    protected override void Dispose(bool disposing)
    {
        ViewModel.Dispose();
    }
}
