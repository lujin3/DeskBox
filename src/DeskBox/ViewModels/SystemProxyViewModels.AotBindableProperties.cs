#if DESKBOX_NATIVE_AOT
namespace DeskBox.ViewModels;

// SystemProxyWidgetContent keeps its runtime Binding surface explicit for
// NativeAOT: only the properties consumed by {Binding} are exposed.
[WinRT.GeneratedBindableCustomProperty([
    nameof(HeaderTitle),
    nameof(IsProxyEnabled),
    nameof(NotConfiguredText),
    nameof(NotConfiguredVisibility),
    nameof(ServerLabel),
    nameof(ServerRowVisibility),
    nameof(ServerText)
], [])]
public sealed partial class SystemProxyWidgetViewModel
{
}
#endif
