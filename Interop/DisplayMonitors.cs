using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Backtrack.Interop;

public readonly record struct DisplayInfo(string DeviceName, bool IsPrimary, Rect BoundsDiu, string? FriendlyName);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

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

            results.Add(new DisplayInfo(info.szDevice, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, bounds, TryGetMonitorFriendlyName(info.szDevice)));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <summary>
    /// Real monitor model name (e.g. "AG276QZD"), the same thing OBS's own log
    /// shows -- not exposed by GetMonitorInfo/EnumDisplayMonitors at all, and
    /// EnumDisplayDevices' own DeviceString is usually just the generic driver
    /// name ("Generic PnP Monitor"), not the actual model. Getting the real
    /// name means walking one level further: EnumDisplayDevices with
    /// EDD_GET_DEVICE_INTERFACE_NAME turns the GDI device name into a device
    /// interface path (\\?\DISPLAY#AOCA601#5&amp;...#{GUID}), whose middle two
    /// segments are exactly the registry path components under
    /// Enum\DISPLAY\...\Device Parameters where Windows stores that monitor's
    /// raw EDID -- and the EDID itself (not the registry, not any Win32 call)
    /// is where the actual model name lives, as one of its descriptor blocks.
    /// Deliberately not using WMI's WmiMonitorID for this (same underlying
    /// data) to avoid pulling in System.Management as a dependency just for
    /// something raw P/Invoke + a registry read already gets us.
    /// Returns null (never throws) if anything along the way doesn't pan out --
    /// callers fall back to a generic "Display N" label in that case.
    /// </summary>
    private static string? TryGetMonitorFriendlyName(string gdiDeviceName)
    {
        try
        {
            var monitorDevice = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(gdiDeviceName, 0, ref monitorDevice, EDD_GET_DEVICE_INTERFACE_NAME))
                return null;

            // DeviceID looks like \\?\DISPLAY#AOCA601#5&2d8cb812&0&UID4353#{GUID} --
            // segments [1] and [2] are the hardware ID and instance ID Windows
            // uses as registry key names under Enum\DISPLAY.
            string[] parts = monitorDevice.DeviceID.Split('#');
            if (parts.Length < 3)
                return null;

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid)
                return null;

            return ParseEdidMonitorName(edid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// EDID descriptor blocks live at fixed 18-byte offsets 54/72/90/108. A
    /// block that isn't a detailed timing descriptor starts with 00 00 00,
    /// followed by a tag byte -- 0xFC specifically means "Monitor Name",
    /// with up to 13 ASCII bytes after that, LF-terminated (0x0A) and
    /// space-padded. Not every monitor includes one (some only have a
    /// serial number or range-limits descriptor instead), hence nullable.
    /// </summary>
    private static string? ParseEdidMonitorName(byte[] edid)
    {
        for (int offset = 54; offset + 18 <= edid.Length && offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 0xFC)
                continue;

            var chars = new List<char>();
            for (int i = offset + 5; i < offset + 18; i++)
            {
                if (edid[i] == 0x0A)
                    break;
                chars.Add((char)edid[i]);
            }

            string name = new string(chars.ToArray()).TrimEnd();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return null;
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
