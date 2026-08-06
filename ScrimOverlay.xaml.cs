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
}
