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
    /// the monitor). The background and the card surface are resolved by the
    /// caller once per break so every monitor shows the same image (a folder
    /// mode re-rolls per break, not per monitor) and the same appearance. The
    /// first live overlay takes keyboard focus so the Enter/Space resume
    /// shortcut works without clicking (Focust focuses its primary prompt
    /// window for the same reason).
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
        foreach (WidgetScreenInfo screen in WidgetScreenCatalog.Capture())
        {
            var overlay = new EyeRestOverlayWindow(
                kind,
                duration,
                background,
                surface,
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
/// One monitor's break overlay, Focust-style (PromptApp.vue): a full-bleed
/// background (solid color or image) under a dim layer, carrying a centered
/// card painted in the app window material design language (Mica/Acrylic/Solid
/// resolved from the global appearance settings, bordered like a widget
/// surface), a radial progress ring around the countdown, a random break
/// suggestion, the prominent accent "back to work" button and a keyboard
/// hint. Enter/Space (or the button) ends the break immediately through the
/// shared deduplicated skip path — the primary overlay holds keyboard focus
/// so the shortcut works without hunting for the button. Window-style
/// surgery copies <see cref="DisplayIdentifyOverlayWindow"/>: borderless
/// topmost tool window hidden from Alt-Tab. Unlike the pre-redesign overlay,
/// the topmost band is actually claimed through
/// <see cref="Win32Helper.SetWindowPos"/>: setting WS_EX_TOPMOST via
/// SetWindowLong only flips the style bit, it does not reorder an already
/// created window, so the overlay used to surface in the normal z-order band.
/// </summary>
internal sealed class EyeRestOverlayWindow
{
    // Fully opaque: the break is meant to hide the desktop entirely — any
    // layered alpha below 0xFF lets desktop text bleed through the background.
    private const byte OverlayAlpha = 0xFF;

    // Focust's radial ring: a 200px circle with a 10px stroke, elapsed arc in
    // near-white over a faint track, countdown centered inside.
    private const double RingSize = 200;
    private const double RingStroke = 10;
    private const double RingCenter = RingSize / 2;
    private const double RingRadius = (RingSize - RingStroke) / 2;

    // Entrance choreography: the content fades in while settling from 96%
    // scale, so the break surfaces instead of popping (Focust transitions its
    // card on mount the same way).
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(240);
    private const double EntranceScale = 0.96;

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TextBlock _countdownText;
    private readonly Path _ringArc;
    private readonly Button _resumeButton;
    private readonly TimeSpan _duration;
    private bool _closed;

    internal bool IsClosed => _closed;

    internal EyeRestOverlayWindow(
        EyeRestBreakKind kind,
        TimeSpan duration,
        EyeRestBreakBackground background,
        EyeRestOverlaySurface surface,
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
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        _countdownText = new TextBlock
        {
            Text = FormatRemaining(duration),
            Foreground = new SolidColorBrush(surface.CountdownColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 44,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        };

        // The track is an inset ellipse (its stroke is center-aligned, so the
        // margin keeps the outer half of the stroke inside the element box);
        // the elapsed arc is a Path rebuilt per tick in UpdateRing.
        var ringTrack = new Ellipse
        {
            Fill = null,
            Stroke = new SolidColorBrush(surface.RingTrackColor),
            StrokeThickness = RingStroke,
            Margin = new Thickness(RingStroke / 2)
        };
        _ringArc = new Path
        {
            Fill = null,
            Stroke = new SolidColorBrush(surface.RingArcColor),
            StrokeThickness = RingStroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var ring = new Grid
        {
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ring.Children.Add(ringTrack);
        ring.Children.Add(_ringArc);
        ring.Children.Add(_countdownText);

        var tip = new TextBlock
        {
            Text = tipText,
            Foreground = new SolidColorBrush(surface.TipColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tip, tipText);

        // The single affordance (deliberately no postpone secondary button):
        // one prominent action ends the break. It is the app's own accent
        // button (AccentButtonStyle + the window-scoped accent resources, the
        // same scoping ThemeService applies to every app window), so rest,
        // hover, pressed and the on-accent label follow the app's design
        // language instead of a bespoke palette — and the label inherits the
        // templated foreground rather than pinning white, which reads wrong
        // whenever the accent runs light.
        _resumeButton = new Button
        {
            Content = new TextBlock
            {
                Text = resumeText,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            },
            Style = ResolveAccentButtonStyle(),
            MinWidth = 220,
            Height = 44,
            Padding = new Thickness(28, 0, 28, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
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
            Spacing = 24
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
        if (background.IsImage && !string.IsNullOrWhiteSpace(background.ImagePath))
        {
            root.Children.Add(CreateBackgroundImage(background.ImagePath!, monitorBounds));
        }

        // The dim layer applies in both modes (Focust dims over solid
        // backgrounds too) so the card text stays readable; its strength
        // follows the effective theme (a light-theme break still dims dark —
        // the rest screen should emit less light, not more).
        root.Children.Add(new Grid
        {
            Background = new SolidColorBrush(surface.DimColor),
            IsHitTestVisible = false
        });

        // Theme parity with the app windows: the overlay root follows the
        // effective light/dark theme (theme resources inside the card, and
        // the accent button's templated states, resolve accordingly) and the
        // accent resources carry the effective accent, not the system
        // default — the same two calls ThemeService.ApplyToWindow makes.
        root.RequestedTheme = surface.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        AccentResourceScope.Apply(
            root,
            App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor);

        // The card surface itself follows the widget material design language:
        // in-app XAML acrylic (a real blur of the dimmed background behind the
        // card) or the app's translucent embedded tint (mica approximation /
        // solid), bordered with the same widget border calculator output.
        Brush cardBackground = surface.CardMaterial == EyeRestOverlayCardMaterial.Acrylic
            ? new AcrylicBrush
            {
                TintColor = surface.CardTintColor,
                TintOpacity = surface.CardTintOpacity,
                TintLuminosityOpacity = surface.CardLuminosityOpacity,
                FallbackColor = surface.CardFallbackColor
            }
            : new SolidColorBrush(surface.CardTintColor);
        var card = new Border
        {
            Background = cardBackground,
            BorderBrush = new SolidColorBrush(surface.CardBorderColor),
            BorderThickness = new Thickness(surface.CardBorderThickness),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(40),
            MaxWidth = 768,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent
        };
        root.Children.Add(card);
        // The entrance animation targets the card.
        FrameworkElement entranceTarget = card;

        // Keyboard parity with Focust's keydown handler: Enter/Space ends the
        // break. PreviewKeyDown tunnels from the root, so it fires before a
        // focused button turns Enter into a second (deduplicated) click.
        // (The WinRT VirtualKey projection has no separate numpad-enter
        // member: the numeric keypad's Enter is the same VK_RETURN, so the
        // Enter arm already covers it.)
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
        // activate the overlay (Focust calls setFocus on its primary window).
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

    /// <summary>Refreshes the countdown display and ring arc (per-tick).</summary>
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
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The XAML island can die with its monitor mid-break; a stale
            // countdown beats crashing the session.
        }
    }

    /// <summary>
    /// Rebuilds the elapsed arc of the progress ring: a clockwise sweep from
    /// 12 o'clock covering <paramref name="elapsedFraction"/> of the circle.
    /// Near-zero renders nothing (track only) and near-full swaps to a whole
    /// ellipse, since a single arc segment cannot express 360°.
    /// </summary>
    private void UpdateRing(double elapsedFraction)
    {
        if (elapsedFraction <= 0.0005)
        {
            _ringArc.Data = null;
            return;
        }

        if (elapsedFraction >= 0.9995)
        {
            _ringArc.Data = new EllipseGeometry
            {
                Center = new Point(RingCenter, RingCenter),
                RadiusX = RingRadius,
                RadiusY = RingRadius
            };
            return;
        }

        double angle = -Math.PI / 2 + elapsedFraction * 2 * Math.PI;
        var figure = new PathFigure
        {
            StartPoint = new Point(RingCenter, RingCenter - RingRadius)
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(
                RingCenter + RingRadius * Math.Cos(angle),
                RingCenter + RingRadius * Math.Sin(angle)),
            Size = new Size(RingRadius, RingRadius),
            IsLargeArc = elapsedFraction > 0.5,
            SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _ringArc.Data = geometry;
    }

    /// <summary>
    /// The app's accent button style (ManagedStorageMigrationDialog precedent
    /// for the resource lookup). A failed lookup falls back to the default
    /// button template, which keeps the resume affordance functional.
    /// </summary>
    private static Style? ResolveAccentButtonStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? resource) &&
        resource is Style style
            ? style
            : null;

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
