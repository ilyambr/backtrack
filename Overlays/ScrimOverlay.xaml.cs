using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class ScrimOverlay : Window
{
    public event Action? Dismissed;

    public ScrimOverlay()
    {
        InitializeComponent();
        Reposition();
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Re-reads the configured display and re-covers it -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    // Any click on the dim area dismisses -- not just left. A right-click with
    // no handler still activates this window at the OS level (any click does,
    // regardless of whether an app-level handler consumes it), and since it's
    // Topmost, that jumped it in front of MainWindow with nothing to put it
    // back, leaving the dimmed background covering everything. Handling
    // right-click the same as left-click sidesteps that: the window closes
    // immediately either way, so there's no state left to get stuck in.
    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Dismissed?.Invoke();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    /// <summary>
    /// Player fullscreen deliberately covers this window's own top-left
    /// corner with the video, but this button sits in a separate Topmost
    /// window from MainWindow -- whether it actually ends up hidden behind
    /// the video depends on exact window bounds/z-order lining up, which
    /// fullscreen's letterboxing can't always guarantee. Collapsing it
    /// outright removes that dependency entirely; Escape and the in-video
    /// fullscreen-exit button both still reach the same close path.
    /// </summary>
    public void SetExitButtonVisible(bool visible) =>
        ExitButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
