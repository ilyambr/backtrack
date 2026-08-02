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
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 20;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }

    /// <summary>
    /// Shows the window, playing the ilyambr -> Backtrack crossfade only the
    /// first time this is called since the app launched -- every later HUD open
    /// this session just shows the Backtrack logo directly, already settled.
    /// </summary>
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

        // ilyambr: fades in by 12%, holds fully visible through 62%, then fades
        // back out to hand off to the Backtrack logo. Opacity only -- no scale/
        // zoom, since animating that on top of a large source image forced a
        // re-rasterize every frame and was the actual cause of the choppiness.
        IlyambrLogo.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(ease,
            (0, 0), (0.12, 1), (0.62, 1), (1.0, 0)));

        // Backtrack: stays hidden until ilyambr starts its exit (58%), then fades
        // in the rest of the way and holds there (BeginAnimation's default
        // FillBehavior keeps the final keyframe's value once it finishes).
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
