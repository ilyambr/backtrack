using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Backtrack.Interop;

namespace Backtrack;

public partial class LogoOverlay : Window
{
    private const double IntroSeconds = 1.25;
    private bool _hasPlayedIntro;

    public LogoOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reposition();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }

    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X + (bounds.Width - Width) / 2;
        Top = bounds.Y + 20;
    }

    public void ShowWithIntro()
    {
        Show();
        if (!_hasPlayedIntro)
        {
            _hasPlayedIntro = true;
            PlayIntro();
        }
        else
        {
            IlyambrLogo.Opacity = 0;
            BacktrackLogo.Opacity = 1;
        }
    }

    private void PlayIntro()
    {
        var ease = new QuadraticEase();

        IlyambrLogo.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(ease,
            (0, 0), (0.12, 1), (0.62, 1), (1.0, 0)));

        BacktrackLogo.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(ease,
            (0, 0), (0.58, 0), (1.0, 1)));
    }

    private static DoubleAnimationUsingKeyFrames BuildTimeline(IEasingFunction ease, params (double time, double value)[] points)
    {
        var timeline = new DoubleAnimationUsingKeyFrames();
        foreach ((double time, double value) in points)
            timeline.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(IntroSeconds * time)), ease));
        return timeline;
    }
}
