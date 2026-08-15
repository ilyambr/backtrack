using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Backtrack.Interop;

/// <summary>
/// Answers the two questions StatusOverlay.Reposition() needs to decide
/// whether to avoid the taskbar or drop to the true screen edge: "is a
/// fullscreen app currently occupying the monitor the indicator itself
/// lives on" and "is the taskbar set to auto-hide at all".
///
/// This is the THIRD design. The first (enumerate every top-level window,
/// anywhere) kept false-positiving on invisible, monitor-sized windows
/// that never actually take focus: Windows 11's own shell-infrastructure
/// host windows (explorer.exe's XamlExplorerHostIslandWindow, TextInputHost's
/// CoreWindow), and other apps' own click-through in-game overlays
/// (Discord's "Discord Overlay" window, and the same trick Steam/GeForce
/// Experience/RTSS/Xbox Game Bar all use). The second (just
/// GetForegroundWindow()) fixed that, but broke the moment a SECOND
/// monitor had real OS focus: a still-fullscreen game sitting unfocused
/// on the indicator's own monitor isn't "foreground" anymore once the user
/// clicks over to a window on a different monitor, even though it's still
/// sitting there fullscreen, taskbar still hidden underneath it.
///
/// This version walks EnumWindows' own natural Z-order (top-to-bottom,
/// same visitation order as the first design) but stops at the FIRST real,
/// non-excluded window that's actually on the TARGET monitor -- i.e.
/// "whatever's currently showing on top of that specific monitor",
/// independent of which monitor real OS focus happens to be on right now.
/// This also naturally covers "Backtrack's own HUD is focused" for free,
/// without a separate special case: Backtrack's own windows are excluded
/// from counting either way, so the walk just continues past them to
/// whatever real window is underneath.
///
/// Also deliberately NOT SHQueryUserNotificationState for the fullscreen
/// check specifically -- it only reports true D3D EXCLUSIVE fullscreen,
/// which most modern games don't actually use; "borderless fullscreen" (a
/// plain chrome-less window sized exactly to the monitor) is invisible to
/// it entirely, and is what actually needed catching here.
/// </summary>
public static class FullscreenDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint ABM_GETSTATE = 0x4;
    private const int ABS_AUTOHIDE = 0x1;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    // Marks a window click-through -- input passes straight to whatever's
    // behind it. Discord/Steam/GeForce Experience/RTSS/Xbox Game Bar's own
    // in-game overlays all use exactly this, which is how "Discord Overlay"
    // ended up ahead of the real game in Z-order and got picked up as the
    // topmost window on the monitor before this filter existed.
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("shell32.dll")]
    private static extern nint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // Explorer and a few of its satellite host processes represent the
    // plain desktop itself while nothing else has focus -- their own
    // top-level window is legitimately monitor-sized (Progman/WorkerW,
    // excluded by class name below), so without this the ordinary "nothing
    // is focused, I'm looking at my desktop" state would itself register
    // as fullscreen.
    // explorer.exe is ambiguous on its own -- it's ALSO the process behind
    // every ordinary File Explorer folder window (class "CabinetWClass")
    // the user might open to browse files, which obviously isn't "the
    // taskbar is up" just because it happens to be focused, even on a
    // completely different monitor than the indicator's own. Used only by
    // IsShellSurfaceActive (IsFullscreenAppOnMonitor's own Z-order walk
    // never needed this distinction -- a folder window isn't monitor-sized
    // either way, so it was never going to false-positive as "fullscreen"
    // there in the first place). Real shell surfaces confirmed live via the
    // diagnostic log (idle desktop, Start menu/Search/Task View/Alt+Tab
    // switcher all sharing "XamlExplorerHostIslandWindow", plus the
    // taskbar's own window classes never actually seen directly foreground
    // but included for completeness).
    private static readonly HashSet<string> ExplorerShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "XamlExplorerHostIslandWindow",
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private static readonly HashSet<string> ShellInfrastructureProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "TextInputHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "PeopleExperienceHost",
        "LockApp",
    };

    // Edge-triggered ("only when it actually changes") diagnostic logging,
    // same shape as the old version's -- Settings > Experimental >
    // Diagnostics > Open Diagnostic Log.
    private static string? _lastLoggedState;

    /// <summary>
    /// True when the taskbar is set to auto-hide at all (a static setting,
    /// not "is it visible on screen at this exact instant") -- an
    /// auto-hidden taskbar never reserves WorkArea space regardless of
    /// whether it's momentarily revealed by a hover, so there's nothing
    /// meaningful to avoid either way.
    /// </summary>
    public static bool IsTaskbarAutoHideEnabled()
    {
        try
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            nint state = SHAppBarMessage(ABM_GETSTATE, ref data);
            return ((int)state & ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the REAL foreground/active window belongs to Windows shell
    /// infrastructure right now -- opening the Start menu, Search, or the
    /// taskbar itself over a fullscreen game genuinely moves real OS focus
    /// to one of these, which is a reliable signal nothing else gives:
    /// unlike IsFullscreenAppOnMonitor's own Z-order walk (which
    /// deliberately skips PAST these same processes, since they can sit
    /// ahead of a normal game in Z-order even during completely ordinary
    /// play, not just when actually summoned), real Windows focus only
    /// ever lands on one of these when the user is genuinely interacting
    /// with it. This is what actually answers "is the taskbar visibly up
    /// right now", separate from "is a fullscreen app still sitting there
    /// underneath everything".
    /// </summary>
    public static bool IsShellSurfaceActive()
    {
        try
        {
            nint fg = GetForegroundWindow();
            if (fg == 0)
                return false;

            GetWindowThreadProcessId(fg, out uint pid);
            string processName = TryGetProcessName(pid);
            bool isShell = ShellInfrastructureProcessNames.Contains(processName);
            if (!isShell)
                return false;

            var classNameBuffer = new StringBuilder(64);
            GetClassName(fg, classNameBuffer, classNameBuffer.Capacity);
            string className = classNameBuffer.ToString();
            var titleBuffer = new StringBuilder(128);
            GetWindowText(fg, titleBuffer, titleBuffer.Capacity);
            string title = titleBuffer.ToString();

            // explorer.exe specifically needs a class check too -- see
            // ExplorerShellWindowClasses' own comment. A regular File
            // Explorer folder window (anywhere, including a completely
            // different monitor than this one) isn't the taskbar.
            if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && !ExplorerShellWindowClasses.Contains(className))
            {
                LogStateIfChanged($"ignoring ordinary explorer.exe window (not a shell surface): class=\"{className}\" title=\"{title}\"");
                return false;
            }

            // The Alt+Tab switcher is hosted by the exact same generic
            // explorer.exe XAML-island class the real Start menu/Search/
            // Task View surfaces use ("XamlExplorerHostIslandWindow"), so
            // it can't be told apart by process or class alone -- found via
            // the diagnostic log flagging its own internal title, "Task
            // Switching", while alt-tabbing kept incorrectly reading as
            // "the taskbar is up" the same way genuinely opening the Start
            // menu does.
            if (title == "Task Switching")
            {
                LogStateIfChanged($"ignoring Alt+Tab switcher (not a real taskbar surface): process={processName}");
                return false;
            }

            LogStateIfChanged($"shell surface active: class=\"{className}\" title=\"{title}\" process={processName}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the topmost real window actually sitting on the given
    /// monitor (matched by DisplayMonitors' own device-name format;
    /// null/empty matches the first real window found on any monitor) is
    /// fullscreen-sized -- independent of whether that window currently has
    /// real OS focus. "Real" excludes Backtrack's own windows, Windows
    /// shell infrastructure, click-through overlays, and the desktop itself
    /// (see their own comments); the walk continues past a window on a
    /// DIFFERENT monitor rather than stopping there, since a window being
    /// earlier in Z-order globally doesn't mean it's the topmost thing on
    /// THIS monitor specifically. Never throws; defaults to "no" on any
    /// failure or if nothing real is found on the monitor at all.
    /// </summary>
    public static bool IsFullscreenAppOnMonitor(string? deviceName)
    {
        try
        {
            int currentProcessId = Environment.ProcessId;
            bool? coversMonitor = null;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                    return true; // keep enumerating

                // Only real top-level windows -- owned windows (tooltips,
                // combo dropdowns, etc.) can report odd/stale bounds and
                // were never "what's showing on this monitor" anyway.
                if (GetWindow(hWnd, GW_OWNER) != 0)
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentProcessId)
                    return true; // never let Backtrack's own windows count -- keep looking for what's underneath

                // Monitor resolved BEFORE the shell/desktop checks below, on
                // purpose: those checks now STOP the walk (not skip past
                // it) once we know the window is genuinely topmost on OUR
                // monitor specifically -- a shell window sitting topmost on
                // some OTHER monitor is irrelevant and must keep the walk
                // going, not stop it early.
                nint hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                    return true;

                if (!string.IsNullOrEmpty(deviceName) && monitorInfo.szDevice != deviceName)
                    return true; // topmost on ITS monitor, not necessarily ours -- keep looking

                string processName = TryGetProcessName(pid);
                if (ShellInfrastructureProcessNames.Contains(processName))
                    return true; // keep looking underneath -- see the class's own comment on why this can't be a "stop" (these sit ahead of a normal game in Z-order too, not just when the Start menu is genuinely open)

                var classNameBuffer = new StringBuilder(64);
                GetClassName(hWnd, classNameBuffer, classNameBuffer.Capacity);
                string className = classNameBuffer.ToString();
                if (className is "Progman" or "WorkerW")
                    return true; // the desktop/shell itself is also monitor-sized

                if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
                    return true; // click-through overlay -- not real content, keep looking for what's underneath it

                // First real, non-excluded window actually on the target
                // monitor -- since EnumWindows visits in Z-order (topmost
                // first), this IS whatever's currently showing on top of
                // that monitor, regardless of where real OS focus is.
                bool gotDwmBounds = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT windowRect, Marshal.SizeOf<RECT>()) == 0;
                if (!gotDwmBounds && !GetWindowRect(hWnd, out windowRect))
                {
                    coversMonitor = false;
                    return false;
                }

                RECT m = monitorInfo.rcMonitor;
                coversMonitor = windowRect.Left <= m.Left && windowRect.Top <= m.Top
                                 && windowRect.Right >= m.Right && windowRect.Bottom >= m.Bottom;

                var titleBuffer = new StringBuilder(128);
                GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
                LogStateIfChanged($"topmost on monitor {monitorInfo.szDevice}: class=\"{className}\" title=\"{titleBuffer}\" process={processName} coversMonitor={coversMonitor}");
                return false; // stop -- found the topmost real window on this monitor
            }, 0);

            if (coversMonitor is null)
                LogStateIfChanged($"nothing real found on monitor {deviceName}");

            return coversMonitor ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static void LogStateIfChanged(string state)
    {
        if (state == _lastLoggedState)
            return;
        _lastLoggedState = state;
        AppLog.Write($"FullscreenDetector: {state}");
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            // Process may have exited between the enumeration and this lookup, or access denied -- not worth failing the whole check over.
            return "unknown";
        }
    }
}
