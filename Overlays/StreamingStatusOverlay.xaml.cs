using System;
using System.Windows;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class StreamingStatusOverlay : Window
{
    public StreamingStatusOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }

    /// <summary>
    /// Centered directly underneath MainWindow's own current bounds -- unlike
    /// LogoOverlay/DisclaimerOverlay, which sit at a fixed spot on the
    /// display independent of MainWindow, this one is meant to read as
    /// attached to the main pill specifically, so it has to follow MainWindow
    /// around every time its size/position changes (compact pill vs. the big
    /// Gallery/Player panel vs. Settings' WideWidth, and moving between
    /// monitors). Called by MainWindow itself right after any of that
    /// changes.
    /// </summary>
    public void Reposition(Rect mainWindowBounds)
    {
        Left = mainWindowBounds.X + (mainWindowBounds.Width - ActualWidth) / 2;
        Top = mainWindowBounds.Y + mainWindowBounds.Height + 8;
    }
}
