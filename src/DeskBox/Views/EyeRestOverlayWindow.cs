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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Views;

/// <summary>
/// Manager for the eye-rest break overlay: one opaque borderless topmost
/// window per attached monitor. Opaque by design — a transparent top-level
/// XAML window renders black (see <see cref="DisplayIdentifyOverlayWindow"/>),
/// and the break must hide the desktop completely, so the layered-window alpha
/// is fully opaque (no see-through of desktop text under the background).
/// </summary>
internal static class EyeRestOverlay
{
    // 16 tips per break kind (EyeRest.Tip.Mini.1..16 / Long.1..16): a wide
    // randomized pool keeps consecutive breaks feeling fresh instead of
    // cycling through four lines.
    private const int TipCountPerKind = 16;

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
    /// the monitor). The background and the surface are resolved by the caller
    /// once per break: image backgrounds decode once and are shared by every
    /// monitor (a folder mode re-rolls per break, not per monitor), and every
    /// monitor carries the same appearance. The first live overlay takes
    /// keyboard focus (forcibly claimed across the Windows foreground lock) so
    /// the Enter/Space resume shortcut works without clicking (Focust focuses
    /// its primary prompt window for the same reason).
    /// </summary>
    public static void ShowAll(
        EyeRestBreakKind kind,
        TimeSpan duration,
        EyeRestBreakBackground background,
        EyeRestOverlaySurface surface,
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

        EyeRestOverlayWindow? primary = null;
        List<WidgetScreenInfo> screens = [.. WidgetScreenCatalog.Capture()];
        // One decode per break shared by every monitor's overlay, bounded to
        // the widest monitor so each window renders 1:1 — per-window decodes
        // would multiply the decoded-bitmap budget for no quality gain.
        int decodeWidth = screens.Count > 0
            ? screens.Max(screen => screen.Monitor.Width)
            : 0;
        BitmapImage? backgroundSource = background.IsImage &&
            !string.IsNullOrWhiteSpace(background.ImagePath)
                ? CreateBackgroundSource(background.ImagePath!, decodeWidth)
                : null;
        foreach (WidgetScreenInfo screen in screens)
        {
            var overlay = new EyeRestOverlayWindow(
                kind,
                duration,
                background,
                surface,
                backgroundSource,
                screen.Monitor,
                localization,
                () => RequestSkip(skipRequested),
                takesFocus: primary is null);
            if (overlay.IsClosed)
            {
                continue;
            }

            primary ??= overlay;
        }
    }

    /// <summary>
    /// Decodes the break background once, bounded to the widest monitor's
    /// physical width, streaming through the loader that keeps the source out
    /// of the framework's URI-keyed cache (a re-picked image must show on the
    /// next break). A failed decode leaves the overlays on the solid
    /// fallback color.
    /// </summary>
    private static BitmapImage? CreateBackgroundSource(string imagePath, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = Math.Clamp(Math.Max(1, decodeWidth), 640, 3840)
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
            return bitmap;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            App.Log($"[EyeRest] Break background image failed to load: {imagePath}: {ex.Message}");
            return null;
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
/// One monitor's break overlay. The raw user background (solid color, or the
/// image cover-fitted to this monitor) sits under the slate-950/35 scrim,
/// carrying the frosted-glass card with the kind title, the HUD countdown
/// core (rotating tick ring, cyan progress arc, inner counter-rotating
/// segment ring, monospace readout), a random break suggestion, the green
/// "back to work" button and a keyboard hint.
/// Enter/Space (or the button) ends the break immediately through the
/// shared deduplicated skip path — the primary overlay holds keyboard focus
/// (forcibly claimed across the Windows foreground lock) so the shortcut
/// works without hunting for the button. Window-style surgery copies
/// <see cref="DisplayIdentifyOverlayWindow"/>: borderless topmost tool
/// window hidden from Alt-Tab. The topmost band is claimed through
/// <see cref="Win32Helper.SetWindowPos"/>: setting WS_EX_TOPMOST via
/// SetWindowLong only flips the style bit, it does not reorder an already
/// created window.
/// </summary>
internal sealed class EyeRestOverlayWindow
{
    // Fully opaque: the break is meant to hide the desktop entirely — any
    // layered alpha below 0xFF lets desktop text bleed through the background.
    private const byte OverlayAlpha = 0xFF;

    // HUD countdown core geometry (all in DIPs, inside a CoreSize square).
    private const double CoreSize = 240;
    private const double CoreCenter = CoreSize / 2;
    private const double ProgressRadius = 98;
    private const double ProgressStroke = 4;

    // Spin periods of the two decorative rings (seconds per revolution).
    private const double TickRingSpinSeconds = 90;
    private const double SegmentRingSpinSeconds = 60;

    // The progress arc and its track come from the overlay surface (the
    // settings coordinator's HUD palette); the other cyan/white HUD details
    // around them are fixed presentation.
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    // Entrance choreography: the content fades in while settling from 96%
    // scale, so the break surfaces instead of popping.
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(240);
    private const double EntranceScale = 0.96;

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _percentText;
    private readonly Path _ringArc;
    private readonly Path _ringGlow;
    private readonly Ellipse _ringHead;
    private readonly TranslateTransform _headOffset = new();
    private readonly Button _resumeButton;
    private readonly TimeSpan _duration;
    private Storyboard? _spinStoryboard;
    private bool _closed;

    internal bool IsClosed => _closed;

    internal EyeRestOverlayWindow(
        EyeRestBreakKind kind,
        TimeSpan duration,
        EyeRestBreakBackground background,
        EyeRestOverlaySurface surface,
        BitmapImage? backgroundSource,
        RectInt32 monitorBounds,
        LocalizationService localization,
        Action skipRequested,
        bool takesFocus)
    {
        _duration = duration;
        string titleText = localization.T(kind == EyeRestBreakKind.Long
            ? "EyeRest.Overlay.LongTitle"
            : "EyeRest.Overlay.MiniTitle");
        string tipText = localization.T(EyeRestOverlay.PickTipKey(kind));
        string resumeText = localization.T("EyeRest.Overlay.Resume");
        string hintText = localization.T("EyeRest.Overlay.ResumeHint");

        var title = new TextBlock
        {
            Text = titleText,
            Foreground = new SolidColorBrush(surface.TitleColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        // The HUD countdown core replaces the old plain progress ring.
        Grid ring = BuildCountdownCore(
            surface,
            duration,
            out _countdownText,
            out _percentText,
            out _ringArc,
            out _ringGlow,
            out _ringHead);

        var tip = new TextBlock
        {
            Text = tipText,
            Foreground = new SolidColorBrush(surface.TipColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tip, tipText);

        // The single affordance (deliberately no postpone secondary button):
        // one prominent action ends the break. The DaisyUI success greens with
        // their hover/pressed shades, 256×48 metrics, 18pt label. Element-level
        // theme resource overrides keep the pointer/pressed states inside that
        // palette (the default template would otherwise swap in the system
        // accent on hover); the label TextBlock deliberately sets NO
        // foreground so the templated states (and Button.Foreground) reach it.
        _resumeButton = new Button
        {
            Content = new TextBlock
            {
                Text = resumeText,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            },
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A)),
            Foreground = new SolidColorBrush(Colors.White),
            MinWidth = 256,
            Height = 48,
            Padding = new Thickness(32, 0, 32, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _resumeButton.Resources["ButtonBackgroundPointerOver"] =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x15, 0x80, 0x3D));
        _resumeButton.Resources["ButtonBackgroundPressed"] =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x65, 0x34));
        _resumeButton.Resources["ButtonForegroundPointerOver"] =
            new SolidColorBrush(Colors.White);
        _resumeButton.Resources["ButtonForegroundPressed"] =
            new SolidColorBrush(Colors.White);
        _resumeButton.Resources["ButtonBorderBrushPointerOver"] =
            new SolidColorBrush(Colors.Transparent);
        _resumeButton.Resources["ButtonBorderBrushPressed"] =
            new SolidColorBrush(Colors.Transparent);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_resumeButton, resumeText);
        _resumeButton.Click += (_, _) => skipRequested();

        var hint = new TextBlock
        {
            Text = hintText,
            Foreground = new SolidColorBrush(surface.HintColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12
        };

        var cardContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 32
        };
        cardContent.Children.Add(title);
        cardContent.Children.Add(ring);
        cardContent.Children.Add(tip);
        cardContent.Children.Add(_resumeButton);
        cardContent.Children.Add(hint);

        var root = new Grid
        {
            Background = new SolidColorBrush(ParseBackgroundColor(background.ColorHex))
        };
        if (backgroundSource is not null)
        {
            root.Children.Add(CreateBackgroundImage(backgroundSource));
        }

        // The slate-950/35 scrim applies in both background modes (it dims over
        // solid backgrounds too) and stays dark in both themes: it keeps the
        // glass card's light text family readable over an arbitrary raw
        // background, and a rest screen should emit less light, not more.
        root.Children.Add(new Grid
        {
            Background = new SolidColorBrush(surface.DimColor),
            IsHitTestVisible = false
        });

        // Theme parity with the app windows: the overlay root follows the
        // effective light/dark theme so the templated button states resolve
        // in the matching chrome.
        root.RequestedTheme = surface.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        // Frosted-glass card: bg-white/10 acrylic with a border-white/10
        // hairline, rounded-3xl, p-10, max-w-3xl, over the scrim. The in-app
        // XAML acrylic gives a REAL blur of the dimmed background behind the
        // panel; the translucent dark fallback covers systems where the blur
        // is unavailable.
        var card = new Border
        {
            Background = new AcrylicBrush
            {
                TintColor = Colors.White,
                TintOpacity = 0.10,
                FallbackColor = Color.FromArgb(0x99, 0x14, 0x14, 0x19)
            },
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(40),
            MaxWidth = 768,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent
        };
        root.Children.Add(card);
        // The entrance animation targets the glass card.
        FrameworkElement entranceTarget = card;

        // Keyboard: Enter/Space ends the break. PreviewKeyDown tunnels from the
        // root, so it fires before a focused button turns Enter into a second
        // (deduplicated) click. (The numeric keypad's Enter is the same
        // VK_RETURN, so the Enter arm already covers it.)
        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter
                or Windows.System.VirtualKey.Space)
            {
                e.Handled = true;
                skipRequested();
            }
        };

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

        // No WS_EX_NOACTIVATE here: the break is meant to take the session
        // over, so clicks and the programmatic focus below must be able to
        // activate the overlay.
        int extendedStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
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
            // windows and the taskbar. SWP_NOACTIVATE keeps the placement
            // itself from stealing focus; the primary overlay activates
            // explicitly afterwards.
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

            StartEntranceAnimation(entranceTarget);
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

        if (takesFocus)
        {
            ActivateAndFocus();
        }
    }

    /// <summary>
    /// Builds the HUD countdown core, three layers over a monospace readout:
    ///   outer  — 90 radial ticks, slowly rotating clockwise;
    ///   middle — cyan progress arc with a soft glow underlay and a bright head dot;
    ///   inner  — 12 coarse segments, slowly rotating counter-clockwise.
    /// The center stack holds a RECOVERY label, the MM:SS readout and the
    /// elapsed percentage.
    /// </summary>
    private Grid BuildCountdownCore(
        EyeRestOverlaySurface surface,
        TimeSpan duration,
        out TextBlock countdown,
        out TextBlock percent,
        out Path arc,
        out Path glow,
        out Ellipse head)
    {
        var grid = new Grid
        {
            Width = CoreSize,
            Height = CoreSize,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Outer: 90 ticks. StrokeDashArray units are multiples of the stroke
        // thickness, so a thick stroke with a thin dash fraction draws short
        // radial marks around the circle.
        Ellipse ticks = MakeDashedRing(
            CoreSize - 8, 8, 90,
            Color.FromArgb(0x66, 0x00, 0xE5, 0xFF), 0.16);

        // Inner: 12 coarse segments, counter-rotating.
        Ellipse segments = MakeDashedRing(
            CoreSize - 64, 3, 12,
            Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.55);

        // Faint full-circle track under the progress arc (surface palette).
        var track = new Ellipse
        {
            Width = ProgressRadius * 2,
            Height = ProgressRadius * 2,
            Stroke = new SolidColorBrush(surface.RingTrackColor),
            StrokeThickness = 1
        };

        // Glow underlay (wide, low alpha) + main arc (thin, bright).
        glow = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0xE5, 0xFF)),
            StrokeThickness = ProgressStroke + 10,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        arc = new Path
        {
            Stroke = new SolidColorBrush(surface.RingArcColor),
            StrokeThickness = ProgressStroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        // Bright head dot riding the end of the arc. It sits at the grid
        // center and is translated onto the circle in UpdateRing.
        head = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0xE5, 0xFF)),
            StrokeThickness = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _headOffset,
            Opacity = 0
        };

        // Center readout.
        var label = new TextBlock
        {
            Text = "RECOVERY",
            FontFamily = MonoFont,
            FontSize = 11,
            CharacterSpacing = 300,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0xE5, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        countdown = new TextBlock
        {
            Text = FormatRemaining(duration),
            FontFamily = MonoFont,
            FontSize = 52,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 60,
            Foreground = new SolidColorBrush(surface.CountdownColor),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        percent = new TextBlock
        {
            Text = "000%",
            FontFamily = MonoFont,
            FontSize = 11,
            CharacterSpacing = 200,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var center = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        center.Children.Add(label);
        center.Children.Add(countdown);
        center.Children.Add(percent);

        grid.Children.Add(ticks);
        grid.Children.Add(segments);
        grid.Children.Add(track);
        grid.Children.Add(glow);
        grid.Children.Add(arc);
        grid.Children.Add(head);
        grid.Children.Add(center);

        StartSpins(ticks, segments);
        return grid;
    }

    /// <summary>
    /// A dashed ring: <paramref name="count"/> evenly spaced dashes, each
    /// covering <paramref name="dashFraction"/> of its slot. The ellipse's
    /// stroke is centered on its box edge, so the path circumference uses
    /// (diameter - thickness).
    /// </summary>
    private static Ellipse MakeDashedRing(
        double diameter, double thickness, int count, Color color, double dashFraction)
    {
        double circumference = Math.PI * (diameter - thickness);
        double period = circumference / count / thickness; // in thickness units
        return new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection
            {
                period * dashFraction,
                period * (1 - dashFraction)
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform()
        };
    }

    /// <summary>
    /// Starts the two decorative rotations in one storyboard. An
    /// animation-engine failure leaves the rings static — the countdown
    /// itself never depends on them.
    /// </summary>
    private void StartSpins(Ellipse tickRing, Ellipse segmentRing)
    {
        try
        {
            var storyboard = new Storyboard();
            AddSpin(storyboard, tickRing, TickRingSpinSeconds, clockwise: true);
            AddSpin(storyboard, segmentRing, SegmentRingSpinSeconds, clockwise: false);
            storyboard.Begin();
            _spinStoryboard = storyboard;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _spinStoryboard = null;
        }
    }

    private static void AddSpin(
        Storyboard storyboard, Ellipse target, double seconds, bool clockwise)
    {
        var spin = new DoubleAnimation
        {
            From = 0,
            To = clockwise ? 360 : -360,
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spin, target);
        Storyboard.SetTargetProperty(
            spin, "(UIElement.RenderTransform).(RotateTransform.Angle)");
        storyboard.Children.Add(spin);
    }

    /// <summary>
    /// Gives the overlay keyboard focus so Enter/Space resume works without a
    /// click. Focus lands on the resume button, which also makes the button's
    /// hover/pressed states and its own Enter/Space activation behave natively.
    /// </summary>
    internal void ActivateAndFocus()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            ForceForegroundForBreak();
            _window.Activate();
            _ = _resumeButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // A focus race during show must not kill the break overlay.
        }
    }

    /// <summary>
    /// Windows denies SetForegroundWindow to a background process, and the
    /// break fires while the user is typing in some other app — so the plain
    /// <see cref="Window.Activate"/> was silently refused: the overlay covered
    /// the screen, but keyboard focus (and therefore the Enter/Space resume
    /// shortcut) stayed with the original foreground app. Attaching our UI
    /// thread's input queue to the foreground thread for the duration of the
    /// activation lifts that lock (the classic AttachThreadInput workaround),
    /// after which the activation and the XAML focus set on the resume button
    /// are honored. A failed attach (e.g. an elevated foreground app) leaves
    /// today's behavior: the overlay shows, only the button click resumes.
    /// </summary>
    private void ForceForegroundForBreak()
    {
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hWnd)
        {
            return;
        }

        uint currentThread = Win32Helper.GetCurrentThreadId();
        uint foregroundThread = Win32Helper.GetWindowThreadProcessId(foreground, out _);
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        if (!Win32Helper.AttachThreadInput(currentThread, foregroundThread, fAttach: true))
        {
            return;
        }

        try
        {
            _ = Win32Helper.SetForegroundWindow(_hWnd);
            _ = Win32Helper.SetFocus(_hWnd);
        }
        finally
        {
            _ = Win32Helper.AttachThreadInput(currentThread, foregroundThread, fAttach: false);
        }
    }

    /// <summary>
    /// Fades the content in from <see cref="EntranceScale"/> so the break
    /// surfaces instead of popping. Any animation-engine failure leaves the
    /// overlay fully visible and unscaled rather than invisible.
    /// </summary>
    private static void StartEntranceAnimation(FrameworkElement target)
    {
        try
        {
            target.Opacity = 0;
            target.RenderTransformOrigin = new Point(0.5, 0.5);
            target.RenderTransform = new ScaleTransform
            {
                ScaleX = EntranceScale,
                ScaleY = EntranceScale
            };

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = EntranceDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var growX = new DoubleAnimation
            {
                From = EntranceScale,
                To = 1,
                Duration = EntranceDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(growX, target);
            Storyboard.SetTargetProperty(growX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
            var growY = new DoubleAnimation
            {
                From = EntranceScale,
                To = 1,
                Duration = EntranceDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(growY, target);
            Storyboard.SetTargetProperty(growY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(growX);
            storyboard.Children.Add(growY);
            storyboard.Begin();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            target.Opacity = 1;
            target.RenderTransform = null;
        }
    }

    /// <summary>Refreshes the countdown readout, percentage and ring (per-tick).</summary>
    internal void UpdateRemaining(TimeSpan remaining)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            _countdownText.Text = FormatRemaining(remaining);
            double elapsed = _duration.TotalSeconds > 0
                ? Math.Clamp(1 - remaining.TotalSeconds / _duration.TotalSeconds, 0, 1)
                : 1;
            UpdateRing(elapsed);
            _percentText.Text = $"{(int)Math.Round(elapsed * 100):000}%";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The XAML island can die with its monitor mid-break; a stale
            // countdown beats crashing the session.
        }
    }

    /// <summary>
    /// Rebuilds the elapsed arc (and its glow twin) and moves the head dot: a
    /// clockwise sweep from 12 o'clock covering <paramref name="elapsedFraction"/>
    /// of the circle. Near-zero renders nothing and near-full swaps to a whole
    /// ellipse, since a single arc segment cannot express 360°.
    /// To make the ring drain instead of fill, pass 1 - elapsed from the caller.
    /// </summary>
    private void UpdateRing(double elapsedFraction)
    {
        if (elapsedFraction <= 0.0005)
        {
            _ringArc.Data = null;
            _ringGlow.Data = null;
            _ringHead.Opacity = 0;
            return;
        }

        bool full = elapsedFraction >= 0.9995;
        Geometry geometry = full
            ? new EllipseGeometry
            {
                Center = new Point(CoreCenter, CoreCenter),
                RadiusX = ProgressRadius,
                RadiusY = ProgressRadius
            }
            : BuildArc(elapsedFraction);
        _ringArc.Data = geometry;
        _ringGlow.Data = geometry;

        double angle = -Math.PI / 2 + elapsedFraction * 2 * Math.PI;
        _headOffset.X = ProgressRadius * Math.Cos(angle);
        _headOffset.Y = ProgressRadius * Math.Sin(angle);
        _ringHead.Opacity = full ? 0 : 1;
    }

    private static PathGeometry BuildArc(double elapsedFraction)
    {
        double angle = -Math.PI / 2 + elapsedFraction * 2 * Math.PI;
        var figure = new PathFigure
        {
            StartPoint = new Point(CoreCenter, CoreCenter - ProgressRadius)
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(
                CoreCenter + ProgressRadius * Math.Cos(angle),
                CoreCenter + ProgressRadius * Math.Sin(angle)),
            Size = new Size(ProgressRadius, ProgressRadius),
            IsLargeArc = elapsedFraction > 0.5,
            SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Full-bleed cover image for ONE monitor: UniformToFill fills the
    /// window exactly (aspect is preserved by cropping, never stretched), so
    /// every monitor is completely covered by the raw background with no
    /// additional zoom or distortion — no virtual-desktop spanning, no
    /// material processing.
    /// </summary>
    private static Image CreateBackgroundImage(BitmapImage source) => new()
    {
        Source = source,
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

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

        // Stop the endless ring rotations before the island goes away.
        try
        {
            _spinStoryboard?.Stop();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
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