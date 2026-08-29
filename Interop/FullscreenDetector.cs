using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Backtrack.Interop;

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

    private static readonly HashSet<string> ExplorerShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "XamlExplorerHostIslandWindow",
        "XamlExplorerHostIslandWindow_WASDK",
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

    private static string? _lastLoggedShellState;
    private static string? _lastLoggedFullscreenState;

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

            if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && !ExplorerShellWindowClasses.Contains(className))
            {
                LogShellStateIfChanged($"ignoring ordinary explorer.exe window (not a shell surface): class=\"{className}\" title=\"{title}\"");
                return false;
            }

            if (title == "Task Switching")
            {
                LogShellStateIfChanged($"ignoring Alt+Tab switcher (not a real taskbar surface): process={processName}");
                return false;
            }

            LogShellStateIfChanged($"shell surface active: class=\"{className}\" title=\"{title}\" process={processName}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFullscreenAppOnMonitor(string? deviceName)
    {
        try
        {
            int currentProcessId = Environment.ProcessId;
            bool? coversMonitor = null;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                    return true;

                if (GetWindow(hWnd, GW_OWNER) != 0)
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentProcessId)
                    return true;

                nint hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                    return true;

                if (!string.IsNullOrEmpty(deviceName) && monitorInfo.szDevice != deviceName)
                    return true;

                string processName = TryGetProcessName(pid);
                if (ShellInfrastructureProcessNames.Contains(processName))
                    return true;

                var classNameBuffer = new StringBuilder(64);
                GetClassName(hWnd, classNameBuffer, classNameBuffer.Capacity);
                string className = classNameBuffer.ToString();
                if (className is "Progman" or "WorkerW")
                    return true;

                if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
                    return true;

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
                LogFullscreenStateIfChanged($"topmost on monitor {monitorInfo.szDevice}: class=\"{className}\" title=\"{titleBuffer}\" process={processName} coversMonitor={coversMonitor}");
                return false;
            }, 0);

            if (coversMonitor is null)
                LogFullscreenStateIfChanged($"nothing real found on monitor {deviceName}");

            return coversMonitor ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static void LogShellStateIfChanged(string state)
    {
        if (state == _lastLoggedShellState)
            return;
        _lastLoggedShellState = state;
        AppLog.Write($"FullscreenDetector: {state}");
    }

    private static void LogFullscreenStateIfChanged(string state)
    {
        if (state == _lastLoggedFullscreenState)
            return;
        _lastLoggedFullscreenState = state;
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
            return "unknown";
        }
    }
}
