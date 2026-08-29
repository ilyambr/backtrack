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

    public void Reposition(Rect mainWindowBounds)
    {
        Left = mainWindowBounds.X + (mainWindowBounds.Width - ActualWidth) / 2;
        Top = mainWindowBounds.Y + mainWindowBounds.Height + 8;
    }
}
