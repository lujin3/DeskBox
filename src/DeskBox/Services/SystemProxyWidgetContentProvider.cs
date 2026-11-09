using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class SystemProxyWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.SystemProxy;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException(
                "System proxy content requires a SystemProxy widget config.",
                nameof(config));
        }

        return new SystemProxyWidgetContentAdapter(
            config,
            context.LocalizationService);
    }
}
