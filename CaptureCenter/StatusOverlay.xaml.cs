using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CaptureCenter.Interop;
using CaptureCenter.Obs;

namespace CaptureCenter;

public partial class StatusOverlay : Window
{
    // Distance (px) at which the fade starts -- full opacity outside this
    // radius, fading down smoothly to fully transparent right at the badges.
    private const double FadeRadius = 140;

    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public StatusOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = SystemParameters.PrimaryScreenWidth - Width - 5;
            Top = 6;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);

            _fadeTimer.Tick += (_, _) => UpdateFadeByProximity();
            _fadeTimer.Start();
        };
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

    public void SetRecording(bool active) => RecBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    public void SetReplayOnline(bool active) => ReplayBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    public void SetMicStatus(MicStatus status)
    {
        MicBadge.Visibility = status == MicStatus.Hidden ? Visibility.Collapsed : Visibility.Visible;
        MicSlash.Visibility = status == MicStatus.MutedOrQuiet ? Visibility.Visible : Visibility.Collapsed;
    }
}
