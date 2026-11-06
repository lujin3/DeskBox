using DeskBox.Contracts;
using DeskBox.Features.EyeRest;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Manager for the eye-rest break overlay: one opaque borderless topmost
/// window per attached monitor. Opaque by design — a transparent top-level
/// XAML window renders black (see <see cref="DisplayIdentifyOverlayWindow"/>),
/// so dimming comes from the layered-window alpha, not the brush.
/// </summary>
internal static class EyeRestOverlay
{
    private const int TipCountPerKind = 4;

    // Registry is internal (not private) so the sibling window class can
    // register/unregister itself — same-assembly only.
    internal static readonly object s_activeOverlaysGate = new();
    internal static readonly List<EyeRestOverlayWindow> s_activeOverlays = [];
    private static bool s_skipRequested;

    /// <summary>Whether a break overlay is currently covering the screens.</summary>
    internal static bool AnyActive
    {
        get
        {
            lock (s_activeOverlaysGate)
            {
                return s_activeOverlays.Count > 0;
            }
        }
    }

    /// <summary>
    /// Covers every attached monitor with the break overlay. A topology change
    /// during the break retires the overlays at once (an overlay positioned on
    /// a monitor being reconfigured would sit on stale coordinates or die with
    /// the monitor).
    /// </summary>
    public static void ShowAll(
        EyeRestBreakKind kind,
        TimeSpan duration,
        LocalizationService localization,
        Action skipRequested)
    {
        // A defensive retire: the timer service never fires BreakDue during a
        // break, but a stale overlay from a failed CloseAll path must not
        // persist under a fresh one.
        CloseAll();

        App.Current.DisplayTopologyChanged -= OnTopologyChangedCloseAll;
        App.Current.DisplayTopologyChanged += OnTopologyChangedCloseAll;

        lock (s_activeOverlaysGate)
        {
            s_skipRequested = false;
        }

        foreach (WidgetScreenInfo screen in WidgetScreenCatalog.Capture())
        {
            var overlay = new EyeRestOverlayWindow(
                kind,
                screen.Monitor,
                screen.EffectiveDpiScale,
                localization,
                () => RequestSkip(skipRequested));
            if (overlay.IsClosed)
            {
                continue;
            }
        }
    }

    /// <summary>Per-tick countdown refresh (break phases only).</summary>
    public static void UpdateAllRemaining(TimeSpan remaining)
    {
        List<EyeRestOverlayWindow> snapshots;
        lock (s_activeOverlaysGate)
        {
            snapshots = [.. s_activeOverlays];
        }

        foreach (EyeRestOverlayWindow overlay in snapshots)
        {
            overlay.UpdateRemaining(remaining);
        }
    }

    /// <summary>Closes every live break overlay. Idempotent.</summary>
    public static void CloseAll()
    {
        List<EyeRestOverlayWindow> snapshots;
        lock (s_activeOverlaysGate)
        {
            snapshots = [.. s_activeOverlays];
            s_activeOverlays.Clear();
        }

        foreach (EyeRestOverlayWindow overlay in snapshots)
        {
            overlay.CloseWindowQuietly();
        }
    }

    private static void OnTopologyChangedCloseAll() => CloseAll();

    private static void RequestSkip(Action skipRequested)
    {
        bool alreadyRequested;
        lock (s_activeOverlaysGate)
        {
            alreadyRequested = s_skipRequested;
            s_skipRequested = true;
        }

        if (alreadyRequested)
        {
            return;
        }

        skipRequested();
    }

    internal static string PickTipKey(EyeRestBreakKind kind)
    {
        string kindKey = kind == EyeRestBreakKind.Long ? "Long" : "Mini";
        int tip = Random.Shared.Next(1, TipCountPerKind + 1);
        return $"EyeRest.Tip.{kindKey}.{tip}";
    }
}

/// <summary>
/// One monitor's break overlay: title, big countdown, a random break
/// suggestion, and a low-visibility skip affordance in the bottom-left corner.
/// Window-style surgery copies <see cref="DisplayIdentifyOverlayWindow"/>:
/// borderless topmost tool window that never activates and is hidden from
/// Alt-Tab.
/// </summary>
internal sealed class EyeRestOverlayWindow
{
    private static readonly Color OverlayBackgroundColor = Color.FromArgb(0xFF, 0x16, 0x16, 0x18);
    private const byte OverlayAlpha = 0xF2; // ~95%: nearly opaque, a hint of the desktop behind

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TextBlock _countdownText;
    private bool _closed;

    internal bool IsClosed => _closed;

    internal EyeRestOverlayWindow(
        EyeRestBreakKind kind,
        RectInt32 monitorBounds,
        double dpiScale,
        LocalizationService localization,
        Action skipRequested)
    {
        string titleText = localization.T(kind == EyeRestBreakKind.Long
            ? "EyeRest.Overlay.LongTitle"
            : "EyeRest.Overlay.MiniTitle");
        string tipText = localization.T(EyeRestOverlay.PickTipKey(kind));

        // The countdown's PHYSICAL height targets ~9% of the monitor (clamped
        // so tiny/large panels stay sane). FontSize is in DIPs and the window
        // renders at this monitor's DPI, so convert px → DIP by dividing by
        // the per-monitor scale, then px height → font size via 72/96 = 0.75.
        double countdownHeightPx = Math.Clamp(monitorBounds.Height * 0.09, 40, 160);
        double dipHeight = countdownHeightPx / (dpiScale > 0 ? dpiScale : 1.0);

        _countdownText = new TextBlock
        {
            Text = FormatRemaining(TimeSpan.Zero),
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = dipHeight * 0.75,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiLight
        };

        var title = new TextBlock
        {
            Text = titleText,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = Math.Clamp(dipHeight * 0.30, 14, 48),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        var tip = new TextBlock
        {
            Text = tipText,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xC8, 0xC8, 0xCC)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = Math.Clamp(dipHeight * 0.24, 12, 32),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Clamp(monitorBounds.Width * 0.6, 240, 1200) / (dpiScale > 0 ? dpiScale : 1.0)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tip, tipText);

        var centerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Math.Clamp(dipHeight * 0.18, 8, 36)
        };
        centerStack.Children.Add(title);
        centerStack.Children.Add(_countdownText);
        centerStack.Children.Add(tip);

        var skipButton = new HyperlinkButton
        {
            Content = new TextBlock { Text = localization.T("EyeRest.Overlay.Skip") },
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x9A, 0x9A, 0x9E)),
            Opacity = 0.55,
            Padding = new Thickness(16, 8, 16, 8)
        };
        skipButton.Click += (_, _) => skipRequested();

        var root = new Grid
        {
            Background = new SolidColorBrush(OverlayBackgroundColor)
        };
        root.Children.Add(centerStack);
        root.Children.Add(new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24),
            Children = { skipButton }
        });

        _window = new Window
        {
            Title = "DeskBox Eye Rest",
            Content = root
        };

        _hWnd = WindowNative.GetWindowHandle(_window);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        int extendedStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
                         Win32Helper.WS_EX_NOACTIVATE |
                         Win32Helper.WS_EX_LAYERED |
                         Win32Helper.WS_EX_TOPMOST;
        _ = Win32Helper.SetWindowLongPtr(
            _hWnd,
            Win32Helper.GWL_EXSTYLE,
            new IntPtr(extendedStyle));
        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_STYLE, new IntPtr(style));

        // A monitor reconfiguration between the style surgery and the AppWindow
        // positioning leaves the target monitor coordinate invalid and AppWindow
        // throws E_NOTFOUND — dropping this screen's overlay beats crashing.
        try
        {
            _appWindow.MoveAndResize(monitorBounds);
            _ = Win32Helper.SetLayeredWindowAttributes(
                _hWnd,
                0,
                OverlayAlpha,
                Win32Helper.LWA_ALPHA);
            _appWindow.Show();
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or ArgumentException)
        {
            _closed = true;
            CloseWindowQuietly();
            App.Log($"[EyeRest] Break overlay failed to place on monitor: {ex.Message}");
            return;
        }

        lock (EyeRestOverlay.s_activeOverlaysGate)
        {
            EyeRestOverlay.s_activeOverlays.Add(this);
        }
    }

    /// <summary>Refreshes the countdown display (per-tick).</summary>
    internal void UpdateRemaining(TimeSpan remaining)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            _countdownText.Text = FormatRemaining(remaining);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The XAML island can die with its monitor mid-break; a stale
            // countdown beats crashing the session.
        }
    }

    private static string FormatRemaining(TimeSpan remaining) =>
        $"{(int)Math.Floor(Math.Max(0, remaining.TotalMinutes)):00}:{Math.Max(0, remaining.Seconds):00}";

    /// <summary>
    /// Close guarded on every path: a topology change can already be tearing
    /// the window down (the XAML island dies with its monitor), and calling
    /// into a half-dead island is exactly where the engine raises stowed
    /// exceptions. Internal (not private) because the EyeRestOverlay manager
    /// owns the registry and calls this from CloseAll.
    /// </summary>
    internal void CloseWindowQuietly()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        lock (EyeRestOverlay.s_activeOverlaysGate)
        {
            EyeRestOverlay.s_activeOverlays.Remove(this);
        }

        try
        {
            _window.Close();
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException)
        {
        }
    }
}
