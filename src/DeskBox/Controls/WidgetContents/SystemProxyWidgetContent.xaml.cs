using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// System-proxy widget content view: a single card-bound mirror of the
/// Windows manual-proxy switch. The switch's user toggles are forwarded to
/// the view model; view-model-driven IsOn updates (external/tray changes and
/// refused toggles) are distinguished by comparing with the mirrored state.
/// </summary>
public sealed partial class SystemProxyWidgetContent : UserControl
{
    private readonly SystemProxyWidgetViewModel _viewModel;

    public SystemProxyWidgetContent(SystemProxyWidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    public SystemProxyWidgetViewModel ViewModel => _viewModel;

    private void ProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        // Binding-driven echoes land here with the switch already equal to
        // the view model's mirror; only a mismatch is user intent.
        if (toggle.IsOn == _viewModel.IsProxyEnabled)
        {
            return;
        }

        _ = _viewModel.SetProxyEnabledAsync(toggle.IsOn);
    }
}
