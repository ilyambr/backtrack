using System;
using System.Runtime.InteropServices;

namespace Backtrack.Interop;

/// <summary>
/// Stops a window from ever becoming the OS-activated window when clicked,
/// without affecting whether it still receives mouse click messages -- WPF's
/// own MouseDown/MouseLeftButtonDown handlers fire exactly as normal, only the
/// side-effect of the OS also granting it activation is suppressed.
///
/// Needed for the Scrim: any click on a Topmost window normally activates it,
/// which jumps it to the front of the topmost band. That's harmless for
/// left-click, since the dismiss handler hides the whole overlay right away
/// regardless -- but a right-click (which is meant to do nothing) was still
/// triggering that activation as an OS-level side effect of the click landing
/// on the window at all, leaving the dimmed background stuck in front of the
/// HUD with no handler to put it back.
/// </summary>
public static class NoActivate
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void Enable(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }
}
