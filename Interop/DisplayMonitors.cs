using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Backtrack.Interop;

public readonly record struct DisplayInfo(string DeviceName, bool IsPrimary, Rect BoundsDiu);

/// <summary>
/// Raw Win32 monitor enumeration, not System.Windows.Forms.Screen -- this app
/// has no other reason to reference WinForms (the tray icon uses raw
/// Shell_NotifyIcon, not NotifyIcon; see SystemTrayManager.cs), so adding that
/// whole assembly just for Screen would be a bigger footprint than a handful
/// of P/Invoke calls.
///
/// Bounds are converted from the physical pixels Win32 reports into WPF's
/// device-independent units (96 DPI) using THAT monitor's own actual DPI
/// (GetDpiForMonitor), not an assumption borrowed from the primary screen --
/// this is what makes multi-monitor placement come out correct even when
/// monitors run at different Windows scaling percentages.
/// </summary>
public static class DisplayMonitors
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

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

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static List<DisplayInfo> GetAll()
    {
        var results = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT rect, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref info))
                return true;

            double scale = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;

            var bounds = new Rect(
                info.rcMonitor.Left / scale,
                info.rcMonitor.Top / scale,
                (info.rcMonitor.Right - info.rcMonitor.Left) / scale,
                (info.rcMonitor.Bottom - info.rcMonitor.Top) / scale);

            results.Add(new DisplayInfo(info.szDevice, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, bounds));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <summary>Falls back to the primary display if deviceName is empty/null, or no longer matches a connected monitor (e.g. it was unplugged).</summary>
    public static DisplayInfo Resolve(string? deviceName)
    {
        List<DisplayInfo> all = GetAll();
        if (!string.IsNullOrEmpty(deviceName))
        {
            DisplayInfo match = all.FirstOrDefault(d => d.DeviceName == deviceName);
            if (!string.IsNullOrEmpty(match.DeviceName))
                return match;
        }
        return all.FirstOrDefault(d => d.IsPrimary, all.FirstOrDefault());
    }

    public static Rect ResolveBoundsDiu(string? deviceName) => Resolve(deviceName).BoundsDiu;
}
