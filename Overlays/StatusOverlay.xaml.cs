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

public enum StatusIndicatorOrientation { Horizontal, Vertical }

public enum StatusIndicatorLocation { TopLeft, TopRight, BottomLeft, BottomRight }

public partial class StatusOverlay : Window
{
    private const double StripLength = 7 * 27 + 6 * 5;
    private const double StripThickness = 27;
    private const double FadeRadius = 140;

    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _repositionPollTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _horizontalLayout = true;
    private Rect _lastLoggedScreenBounds;

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

    public void Reposition()
    {
        AppSettings settings = AppSettings.Load();
        ApplyLayout(settings.StatusIndicatorOrientation, settings.StatusIndicatorLocation);

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
