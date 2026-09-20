using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

using Backtrack.Core;

namespace Backtrack.Interop;

public readonly record struct DisplayInfo(string DeviceName, string? DeviceId, bool IsPrimary, Rect BoundsDiu, Rect WorkAreaDiu, string? FriendlyName);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int SetProcessDpiAwareness(int awareness);

    private static readonly IntPtr DpiContextPerMonitorAwareV2 = new(-4);

    public static void EnsureDpiAwareness()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(DpiContextPerMonitorAwareV2))
            {
                // Fallback to Per-Monitor V1 if V2 is unavailable
                SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            }
        }
        catch
        {
            try { SetProcessDpiAwareness(2); } catch { }
        }
    }

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

            var workArea = new Rect(
                info.rcWork.Left / scale,
                info.rcWork.Top / scale,
                (info.rcWork.Right - info.rcWork.Left) / scale,
                (info.rcWork.Bottom - info.rcWork.Top) / scale);

            (string? deviceId, string? friendlyName) = QueryMonitorDetails(info.szDevice);

            results.Add(new DisplayInfo(
                info.szDevice,
                deviceId,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                bounds,
                workArea,
                friendlyName));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    private static (string? DeviceId, string? FriendlyName) QueryMonitorDetails(string gdiDeviceName)
    {
        try
        {
            var monitorDevice = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(gdiDeviceName, 0, ref monitorDevice, EDD_GET_DEVICE_INTERFACE_NAME))
                return (null, null);

            string? deviceId = string.IsNullOrWhiteSpace(monitorDevice.DeviceID) ? null : monitorDevice.DeviceID.Trim();
            string? friendlyName = null;

            if (!string.IsNullOrEmpty(deviceId))
            {
                string[] parts = deviceId.Split('#');
                if (parts.Length >= 3)
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                    if (key?.GetValue("EDID") is byte[] edid)
                    {
                        friendlyName = ParseEdidMonitorName(edid);
                    }
                }
            }

            return (deviceId, friendlyName);
        }
        catch
        {
            return (null, null);
        }
    }

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

    public static DisplayInfo Resolve(string? deviceName, string? deviceId = null, string? friendlyName = null)
    {
        List<DisplayInfo> all = GetAll();
        if (all.Count == 0)
            return default;

        // 1. Try hardware DeviceId match (strongest persistent identity across GDI renumbering/unplugs)
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            string targetId = deviceId.Trim();
            DisplayInfo matchById = all.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.DeviceId) &&
                (string.Equals(d.DeviceId.Trim(), targetId, StringComparison.OrdinalIgnoreCase) ||
                 IsDeviceIdMatch(d.DeviceId, targetId)));

            if (!string.IsNullOrEmpty(matchById.DeviceName))
                return matchById;
        }

        // 2. Try FriendlyName match (e.g. "VG2248")
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            string targetName = friendlyName.Trim();
            DisplayInfo matchByName = all.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.FriendlyName) &&
                string.Equals(d.FriendlyName.Trim(), targetName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(matchByName.DeviceName))
                return matchByName;
        }

        // 3. Try GDI DeviceName / identifier match
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            string cleanTarget = deviceName.Trim().TrimEnd('\0', ' ');
            DisplayInfo match = all.FirstOrDefault(d =>
                string.Equals(d.DeviceName.Trim().TrimEnd('\0', ' '), cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(d.FriendlyName) && string.Equals(d.FriendlyName.Trim(), cleanTarget, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.DeviceId) && string.Equals(d.DeviceId.Trim(), cleanTarget, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrEmpty(match.DeviceName))
                return match;
        }

        // 4. Fallback: Preferred display is physically unplugged; fall back to Primary or first available display
        return all.FirstOrDefault(d => d.IsPrimary, all.FirstOrDefault());
    }

    public static DisplayInfo Resolve(AppSettings? settings)
    {
        if (settings is null)
            return Resolve((string?)null);
        return Resolve(settings.DisplayDeviceName, settings.DisplayDeviceId, settings.DisplayFriendlyName);
    }

    private static bool IsDeviceIdMatch(string id1, string id2)
    {
        string[] p1 = id1.Split('#');
        string[] p2 = id2.Split('#');
        if (p1.Length >= 2 && p2.Length >= 2)
        {
            if (string.Equals(p1[1], p2[1], StringComparison.OrdinalIgnoreCase))
            {
                if (p1.Length >= 3 && p2.Length >= 3)
                    return string.Equals(p1[2], p2[2], StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        return false;
    }

    public static Rect ResolveBoundsDiu(string? deviceName) => Resolve(deviceName, null, null).BoundsDiu;
    public static Rect ResolveBoundsDiu(AppSettings? settings) => Resolve(settings).BoundsDiu;
    public static Rect ResolveWorkAreaDiu(string? deviceName) => Resolve(deviceName, null, null).WorkAreaDiu;
    public static Rect ResolveWorkAreaDiu(AppSettings? settings) => Resolve(settings).WorkAreaDiu;
}
