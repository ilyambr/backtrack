using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Backtrack.Interop;
using Backtrack.Obs;

namespace Backtrack;

/// <summary>Which axis the status badges lay out along. Settings > Status Indicators > Orientation.</summary>
public enum StatusIndicatorOrientation { Horizontal, Vertical }

/// <summary>Which screen corner the status indicator anchors to. Settings > Status Indicators > Location.</summary>
public enum StatusIndicatorLocation { TopLeft, TopRight, BottomLeft, BottomRight }

public partial class StatusOverlay : Window
{
    // 7 badges * 27px + 6 * 5px gaps between them.
    private const double StripLength = 7 * 27 + 6 * 5;
    private const double StripThickness = 27;
    // Distance (px) at which the fade starts -- full opacity outside this
    // radius, fading down smoothly to fully transparent right at the badges.
    private const double FadeRadius = 140;

    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    // Neither a real taskbar dock/undock nor a fullscreen app starting/
    // stopping raises any event this app can subscribe to (unlike
    // MainWindow's own SystemEvents.DisplaySettingsChanged hook for actual
    // resolution changes) -- a light poll is the only way to notice either
    // one happened and re-anchor accordingly. Reposition() itself is cheap
    // (a couple of Win32 calls plus a handful of property sets that no-op
    // when unchanged) -- 300ms was picked after 1s felt noticeably laggy
    // in practice (launching a game and visibly waiting up to a full
    // second for the indicator to drop down reads as broken, not just slow).
    private readonly DispatcherTimer _repositionPollTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Set by ApplyLayout, read by RefreshBadgeMargins whenever a badge's own
    // Visibility changes later (SetRecording etc.) -- avoids an AppSettings.Load()
    // disk read on every single status tick just to know which gap Thickness to use.
    private bool _horizontalLayout = true;
    // Edge-triggered diagnostic logging for Reposition()'s own bounds math --
    // same "log it instead of guessing again" approach FullscreenDetector's
    // own _lastLoggedCulprit uses, here for whenever the WorkArea/Bounds
    // rect actually used to position this window changes.
    private Rect _lastLoggedScreenBounds;

    /// <summary>
    /// Set by MainWindow (ToggleVisible/CloseOverlay) whenever the HUD
    /// itself opens or closes. While it's open, this alone forces the
    /// indicator to the true screen edge, taskbar or not -- Backtrack's own
    /// overlay draws on top of everything else regardless of where the
    /// taskbar is, so there's nothing for it to actually collide with, and
    /// this is a simpler, always-correct answer than trying to figure out
    /// whether something ELSE happens to still be fullscreen underneath it
    /// (which is what FullscreenDetector.IsFullscreenAppOnMonitor is for --
    /// still used below for when the HUD is closed).
    /// </summary>
    public bool IsHudOpen { get; set; }

    public StatusOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reposition();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);

            _fadeTimer.Tick += (_, _) => UpdateFadeByProximity();
            _fadeTimer.Start();

            _repositionPollTimer.Tick += (_, _) => Reposition();
            _repositionPollTimer.Start();
        };
    }

    /// <summary>Re-reads the configured display AND the orientation/location/corner settings -- called again from Settings whenever any of those change mid-session, not just the display, and from _repositionPollTimer every second to catch a taskbar/fullscreen change nothing else notifies this window about.</summary>
    public void Reposition()
    {
        AppSettings settings = AppSettings.Load();
        ApplyLayout(settings.StatusIndicatorOrientation, settings.StatusIndicatorLocation);

        // Drop straight to the true monitor edge (BoundsDiu, taskbar
        // ignored) whenever the HUD itself is open (see IsHudOpen's own
        // comment -- unconditional, doesn't matter what's on screen behind
        // it), OR a fullscreen app is the topmost real thing on THIS
        // indicator's own monitor (independent of which monitor real OS
        // focus is actually on right now -- see IsFullscreenAppOnMonitor's
        // own comment) AND the taskbar/Start menu/Search isn't the thing
        // that ACTUALLY has real focus right now (IsShellSurfaceActive --
        // opening the taskbar over a fullscreen game moves real Windows
        // focus there, which the Z-order walk alone can't see: it
        // deliberately skips past these same processes since they can sit
        // ahead of a normal game in Z-order even during ordinary play).
        // Otherwise avoid the taskbar (WorkAreaDiu) UNLESS it's set to
        // auto-hide, which never reserves space to avoid in the first place
        // regardless of whether it's currently peeking out.
        bool dropToEdge = IsHudOpen ||
            (!FullscreenDetector.IsShellSurfaceActive() && FullscreenDetector.IsFullscreenAppOnMonitor(settings.DisplayDeviceName));
        bool autoHide = !dropToEdge && FullscreenDetector.IsTaskbarAutoHideEnabled();
        Rect boundsDiu = DisplayMonitors.ResolveBoundsDiu(settings.DisplayDeviceName);
        Rect workAreaDiu = DisplayMonitors.ResolveWorkAreaDiu(settings.DisplayDeviceName);
        Rect screenBounds = dropToEdge || autoHide ? boundsDiu : workAreaDiu;
        if (screenBounds != _lastLoggedScreenBounds)
        {
            _lastLoggedScreenBounds = screenBounds;
            AppLog.Write($"StatusOverlay.Reposition: location={settings.StatusIndicatorLocation} dropToEdge={dropToEdge} autoHide={autoHide} "
                + $"boundsDiu={boundsDiu} workAreaDiu={workAreaDiu} using={(dropToEdge || autoHide ? "bounds" : "workArea")}");
        }
        bool isLeft = settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.BottomLeft;
        bool isTop = settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.TopRight;
        Left = isLeft ? screenBounds.X + 6 : screenBounds.X + screenBounds.Width - Width - 6;
        Top = isTop ? screenBounds.Y + 6 : screenBounds.Y + screenBounds.Height - Height - 6;
    }

    /// <summary>
    /// The strip is a fixed size (room for all badges) regardless of
    /// orientation or how many badges happen to be visible right now --
    /// SizeToContent would need Reposition() re-run on every single
    /// SetRecording/SetStreaming/SetVirtualCamActive/SetReplayOnline/
    /// SetMicStatus/SetObsDisconnected/SetEncoderOverloaded call just to
    /// stay anchored to a right/bottom corner as the content shrinks or
    /// grows, since a resize on those edges moves the window's own Left/Top.
    /// A fixed frame means only BadgesPanel's alignment (toward whichever
    /// edge(s) the corner setting points at) needs to change; the individual
    /// badges already collapse in place within it, same as the original
    /// hardcoded top-right layout always relied on (see the XAML's own
    /// comment on why a fixed frame + alignment, not resize, is deliberate).
    /// </summary>
    private void ApplyLayout(StatusIndicatorOrientation orientation, StatusIndicatorLocation location)
    {
        bool horizontal = orientation == StatusIndicatorOrientation.Horizontal;
        _horizontalLayout = horizontal;
        Width = horizontal ? StripLength : StripThickness;
        Height = horizontal ? StripThickness : StripLength;

        bool isLeft = location is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.BottomLeft;
        bool isTop = location is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.TopRight;

        BadgesPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        BadgesPanel.HorizontalAlignment = horizontal ? (isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Stretch;
        BadgesPanel.VerticalAlignment = horizontal ? VerticalAlignment.Stretch : (isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom);

        RefreshBadgeMargins();
    }

    /// <summary>
    /// Gives every VISIBLE badge a leading margin except the first VISIBLE
    /// one, not just the first one BY INDEX -- badges collapse independently
    /// (SetRecording/SetStreaming/etc., not just at layout time), so with a
    /// plain "index 0 gets no margin" rule, whichever badge happens to be
    /// first in the StackPanel (Rec) getting hidden left the next visible
    /// one (Stream) still carrying its normal leading-gap margin, reading as
    /// a real dead gap between the strip's own top/left edge and its first
    /// visible badge (worse in vertical mode, where that space reads as a
    /// gap down from the corner rather than a subtle horizontal nudge).
    /// Called from ApplyLayout (orientation/corner changed) and from every
    /// Set* method below (a badge's own visibility changed).
    /// </summary>
    private void RefreshBadgeMargins()
    {
        Thickness gap = _horizontalLayout ? new Thickness(5, 0, 0, 0) : new Thickness(0, 5, 0, 0);
        bool seenVisible = false;
        foreach (UIElement child in BadgesPanel.Children)
        {
            if (child is not FrameworkElement badge)
                continue;
            bool isFirstVisible = !seenVisible && badge.Visibility == Visibility.Visible;
            badge.Margin = isFirstVisible ? default : gap;
            seenVisible |= badge.Visibility == Visibility.Visible;
        }
    }

    /// <summary>Eases Opacity toward a target based on cursor distance each tick, instead of snapping Visibility on enter/leave.</summary>
    private void UpdateFadeByProximity()
    {
        var bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        double distance = DistanceToRect(CursorPos.Get(), bounds);
        double target = Math.Clamp(distance / FadeRadius, 0, 1);
        Opacity += (target - Opacity) * 0.25;
    }

    private static double DistanceToRect(Point p, Rect r)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void SetObsDisconnected(bool disconnected)
    {
        ObsDisconnectedBadge.Visibility = disconnected ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }

    /// <summary>
    /// Dark-red/light-red flashing warning-triangle, shown for as long as
    /// MainWindow's own edge-triggered overload check (see its
    /// EncoderOverloadDetected handler) considers a real overload ongoing.
    /// The animation itself is built here, not in XAML, because it needs to
    /// flash between two THEMED colors (RecDark/Rec) -- a plain XAML
    /// Storyboard can't read DynamicResource values, and the two colors are
    /// read fresh from Application.Current.Resources each time this turns on
    /// rather than cached, same reasoning as ToastOverlay's own
    /// dynamically-built elements (see CLAUDE.md's theming section).
    /// </summary>
    public void SetEncoderOverloaded(bool overloaded)
    {
        if (overloaded)
        {
            Color dark = ((SolidColorBrush)Application.Current.Resources["RecDark"]).Color;
            Color light = ((SolidColorBrush)Application.Current.Resources["Rec"]).Color;
            var brush = new SolidColorBrush(dark);
            EncoderOverloadIconPath.Fill = brush;

            var flash = new ColorAnimation
            {
                From = dark,
                To = light,
                Duration = TimeSpan.FromMilliseconds(600),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, flash);

            EncoderOverloadBadge.Visibility = Visibility.Visible;
        }
        else
        {
            // Stop the clock explicitly rather than just collapsing the
            // badge -- a Forever animation on a brush nobody's looking at
            // anymore is a small but pointless ongoing CPU cost. (The next
            // time this turns back on, a brand new brush is created above,
            // so this isn't needed to get a clean dark-red restart -- it's
            // purely to actually stop the old clock.)
            if (EncoderOverloadIconPath.Fill is SolidColorBrush activeBrush)
                activeBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            EncoderOverloadBadge.Visibility = Visibility.Collapsed;
        }
        RefreshBadgeMargins();
    }

    public void SetRecording(bool active)
    {
        RecBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }

    public void SetStreaming(bool active)
    {
        StreamBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }

    public void SetReplayOnline(bool active)
    {
        ReplayBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }

    public void SetVirtualCamActive(bool active)
    {
        VirtualCamBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }

    private MicStatus? _lastLoggedMicStatus;

    public void SetMicStatus(MicStatus status)
    {
        // Edge-triggered diagnostic logging (called every 1s from
        // MainWindow's _micTimer regardless of change) -- Settings >
        // Experimental > Diagnostics > Open Diagnostic Log.
        if (status != _lastLoggedMicStatus)
        {
            _lastLoggedMicStatus = status;
            AppLog.Write($"StatusOverlay.SetMicStatus: {status}");
        }
        MicBadge.Visibility = status == MicStatus.Hidden ? Visibility.Collapsed : Visibility.Visible;
        MicSlash.Visibility = status == MicStatus.MutedOrQuiet ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadgeMargins();
    }
}
