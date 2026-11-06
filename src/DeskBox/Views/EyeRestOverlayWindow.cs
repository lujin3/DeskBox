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
using Microsoft.UI.Xaml.Media.Imaging;
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
    /// the monitor). The background is resolved by the caller once per break
    /// so every monitor shows the same image (a folder mode re-rolls per
    /// break, not per monitor).
    /// </summary>
    public static void ShowAll(
        EyeRestBreakKind kind,
        TimeSpan duration,
        EyeRestBreakBackground background,
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
                duration,
                background,
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
/// One monitor's break overlay, Focust-style: a full-screen background (solid
/// color or image with a dim layer) with a centered rounded card holding the
/// kind label, big countdown, a random break suggestion, a remaining-time
/// progress bar, and one prominent "back to work" button that ends the break
/// immediately (skip). Window-style surgery copies
/// <see cref="DisplayIdentifyOverlayWindow"/>: borderless topmost tool window
/// that never activates and is hidden from Alt-Tab. Unlike the pre-redesign
/// overlay, the topmost band is actually claimed through
/// <see cref="Win32Helper.SetWindowPos"/>: setting WS_EX_TOPMOST via
/// SetWindowLong only flips the style bit, it does not reorder an already
/// created window, so the overlay used to surface in the normal z-order band.
/// </summary>
internal sealed class EyeRestOverlayWindow
{
    private const byte OverlayAlpha = 0xF2; // ~95%: nearly opaque, a hint of the desktop behind

    // Focust-flavored card palette; the card floats over the background with a
    // translucent dark scrim so both solid and image modes stay readable.
    private static readonly Color CardBackgroundColor = Color.FromArgb(0x80, 0x14, 0x14, 0x19);
    private static readonly Color CardBorderColor = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Color ImageDimColor = Color.FromArgb(0x59, 0x0F, 0x17, 0x2A);
    private static readonly Color ResumeButtonColor = Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TextBlock _countdownText;
    private readonly ProgressBar _remainingBar;
    private readonly TimeSpan _duration;
    private bool _closed;

    internal bool IsClosed => _closed;

    internal EyeRestOverlayWindow(
        EyeRestBreakKind kind,
        TimeSpan duration,
        EyeRestBreakBackground background,
        RectInt32 monitorBounds,
        double dpiScale,
        LocalizationService localization,
        Action skipRequested)
    {
        _duration = duration;
        string titleText = localization.T(kind == EyeRestBreakKind.Long
            ? "EyeRest.Overlay.LongTitle"
            : "EyeRest.Overlay.MiniTitle");
        string tipText = localization.T(EyeRestOverlay.PickTipKey(kind));
        string resumeText = localization.T("EyeRest.Overlay.Resume");

        // The countdown's PHYSICAL height targets ~9% of the monitor (clamped
        // so tiny/large panels stay sane). FontSize is in DIPs and the window
        // renders at this monitor's DPI, so convert px → DIP by dividing by
        // the per-monitor scale, then px height → font size via 72/96 = 0.75.
        double countdownHeightPx = Math.Clamp(monitorBounds.Height * 0.09, 40, 160);
        double dipHeight = countdownHeightPx / (dpiScale > 0 ? dpiScale : 1.0);

        _countdownText = new TextBlock
        {
            Text = FormatRemaining(duration),
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

        _remainingBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            Width = Math.Clamp(dipHeight * 2.4, 220, 420),
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(2)
        };

        // The single affordance (deliberately no postpone secondary button):
        // one prominent action ends the break, mirroring Focust's primary
        // "resume" button. It routes through the same deduplicated skip path
        // the retired corner hyperlink used.
        var resumeButton = new Button
        {
            Content = new TextBlock
            {
                Text = resumeText,
                FontSize = Math.Clamp(dipHeight * 0.28, 15, 22),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            },
            Background = new SolidColorBrush(ResumeButtonColor),
            Foreground = new SolidColorBrush(Colors.White),
            MinWidth = Math.Clamp(dipHeight * 2.0, 200, 320),
            Height = Math.Clamp(dipHeight * 0.52, 42, 60),
            Padding = new Thickness(32, 0, 32, 0),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(resumeButton, resumeText);
        resumeButton.Click += (_, _) => skipRequested();

        var cardContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Math.Clamp(dipHeight * 0.18, 10, 32)
        };
        cardContent.Children.Add(title);
        cardContent.Children.Add(_countdownText);
        cardContent.Children.Add(tip);
        cardContent.Children.Add(_remainingBar);
        cardContent.Children.Add(resumeButton);

        var card = new Border
        {
            Background = new SolidColorBrush(CardBackgroundColor),
            BorderBrush = new SolidColorBrush(CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(44, 36, 44, 36),
            MaxWidth = Math.Clamp(monitorBounds.Width * 0.6, 480, 960) / (dpiScale > 0 ? dpiScale : 1.0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(ParseBackgroundColor(background.ColorHex))
        };
        if (background.IsImage && !string.IsNullOrWhiteSpace(background.ImagePath))
        {
            root.Children.Add(CreateBackgroundImage(background.ImagePath!, monitorBounds));
            root.Children.Add(new Grid
            {
                Background = new SolidColorBrush(ImageDimColor),
                IsHitTestVisible = false
            });
        }

        // The card (with its scrim) sits above the background layers; its
        // input stays live so the resume button is clickable.
        root.Children.Add(card);
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
            // The actual topmost claim: SetWindowLong only flips the style bit
            // (the docs require SetWindowPos to reorder), so this one call is
            // what puts the overlay above every normal window, other topmost
            // windows and the taskbar — and shows it without stealing focus.
            bool shown = Win32Helper.SetWindowPos(
                _hWnd,
                Win32Helper.HWND_TOPMOST,
                monitorBounds.X,
                monitorBounds.Y,
                monitorBounds.Width,
                monitorBounds.Height,
                Win32Helper.SWP_SHOWWINDOW |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);
            if (!shown)
            {
                _appWindow.Show();
                _ = Win32Helper.SetWindowPos(
                    _hWnd,
                    Win32Helper.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOACTIVATE |
                    Win32Helper.SWP_FRAMECHANGED);
            }
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
            _remainingBar.Value = _duration.TotalSeconds > 0
                ? Math.Clamp(remaining.TotalSeconds / _duration.TotalSeconds, 0, 1)
                : 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The XAML island can die with its monitor mid-break; a stale
            // countdown beats crashing the session.
        }
    }

    /// <summary>
    /// Full-bleed cover image. The decode is bounded to the monitor's physical
    /// width so a full-resolution wallpaper never enters the decoded-bitmap
    /// budget; the stream loader keeps the source out of the framework's
    /// URI-keyed cache (a re-picked image must show on the next break).
    /// </summary>
    private static Image CreateBackgroundImage(string imagePath, RectInt32 monitorBounds)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = Math.Clamp(monitorBounds.Width, 640, 3840)
        };
        WidgetImageStreamLoader.Fill(
            bitmap,
            imagePath,
            settled =>
            {
                if (!settled)
                {
                    App.Log($"[EyeRest] Break background image failed to decode: {imagePath}");
                }
            });

        return new Image
        {
            Source = bitmap,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    /// <summary>Parses a #RRGGBB string (factory-normalized) with the dark default fallback.</summary>
    private static Color ParseBackgroundColor(string colorHex)
    {
        string hex = EyeRestBreakBackgroundFactory.NormalizeColorHex(colorHex);
        return Color.FromArgb(
            0xFF,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
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
