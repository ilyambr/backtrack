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

    /// <summary>Shows the window and plays the ilyambr -> Backtrack crossfade -- called every time the HUD opens, matching how the reference plays it on each fresh reveal rather than once per app lifetime.</summary>
    public void ShowWithIntro()
    {
        Show();
        PlayIntro();
    }

    private void PlayIntro()
    {
        var ease = new QuadraticEase();

        // ilyambr: fades/scales in by 12%, holds fully visible through 62%, then
        // fades/scales back out to hand off to the Backtrack logo.
        Animate(IlyambrLogo, IlyambrScale, ease,
            opacity: new (double, double)[] { (0, 0), (0.12, 1), (0.62, 1), (1.0, 0) },
            scale: new (double, double)[] { (0, 0.96), (0.12, 1), (0.62, 1), (1.0, 0.98) });

        // Backtrack: stays hidden until ilyambr starts its exit (58%), then
        // fades/scales in the rest of the way and holds there (BeginAnimation's
        // default FillBehavior keeps the final keyframe's value once it finishes).
        Animate(BacktrackLogo, BacktrackScale, ease,
            opacity: new (double, double)[] { (0, 0), (0.58, 0), (1.0, 1) },
            scale: new (double, double)[] { (0, 0.98), (0.58, 0.98), (1.0, 1) });
    }

    private static void Animate(UIElement target, ScaleTransform scaleTransform, IEasingFunction ease,
        (double time, double value)[] opacity, (double time, double value)[] scale)
    {
        target.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(opacity, ease));
        DoubleAnimationUsingKeyFrames scaleTimeline = BuildTimeline(scale, ease);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleTimeline);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleTimeline.Clone());
    }

    private static DoubleAnimationUsingKeyFrames BuildTimeline((double time, double value)[] points, IEasingFunction ease)
    {
        var timeline = new DoubleAnimationUsingKeyFrames();
        foreach ((double time, double value) in points)
            timeline.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(IntroSeconds * time)), ease));
        return timeline;
    }
}
