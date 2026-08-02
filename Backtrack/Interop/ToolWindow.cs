using System;
using System.Runtime.InteropServices;

namespace Backtrack.Interop;

/// <summary>
/// Keeps a window out of Alt+Tab. ShowInTaskbar="False" alone only hides a window
/// from the taskbar -- Alt+Tab decides inclusion from the WS_EX_TOOLWINDOW/
/// WS_EX_APPWINDOW extended styles instead, which is why every one of this app's
/// windows (MainWindow plus the Status/Toast/Scrim/Disclaimer/Logo overlays) was
/// showing up as five or six separate entries despite ShowInTaskbar being off.
/// </summary>
public static class ToolWindow
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void Enable(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW;
        style &= ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }
}
