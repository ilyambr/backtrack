using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Backtrack;

public class SystemTrayManager : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Window _owner;
    private readonly IntPtr _hwnd;
    private NOTIFYICONDATA _nid;
    private System.Drawing.Bitmap? _baseBitmap;
    private IntPtr _currentHIcon = IntPtr.Zero;
    private bool _obsConnected;
    private bool _statusOverlayVisible = true;
    private bool _added;

    public event Action? OnOpenHudRequested;
    public event Action? OnOpenSettingsRequested;
    public event Action? OnOpenClipsFolderRequested;
    public event Action? OnToggleStatusOverlayRequested;
    public event Action? OnQuitRequested;

    public SystemTrayManager(Window owner)
    {
        _owner = owner;
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        HwndSource source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string assetPath = Path.Combine(baseDir, "Assets", "backtrack_tray.png");
            if (!File.Exists(assetPath))
            {
                assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "backtrack_tray.png");
            }

            if (File.Exists(assetPath))
            {
                _baseBitmap = new System.Drawing.Bitmap(assetPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load tray image: {ex.Message}");
        }

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            szTip = "Backtrack"
        };

        UpdateTrayIcon(obsConnected: false);
    }

    public void UpdateStatus(bool obsConnected, bool statusOverlayVisible)
    {
        _obsConnected = obsConnected;
        _statusOverlayVisible = statusOverlayVisible;
        UpdateTrayIcon(obsConnected);
    }

    private void UpdateTrayIcon(bool obsConnected)
    {
        try
        {
            int size = 32;
            using var bmp = new System.Drawing.Bitmap(size, size);
            using var g = System.Drawing.Graphics.FromImage(bmp);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            if (_baseBitmap != null)
            {
                g.DrawImage(_baseBitmap, 0, 0, size, size);
            }
            else
            {
                g.Clear(System.Drawing.Color.Transparent);
            }

            // Green / Red dot in bottom-right corner
            int dotSize = 10;
            int dotX = size - dotSize - 1;
            int dotY = size - dotSize - 1;

            using var ringBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 20, 21, 24));
            g.FillEllipse(ringBrush, dotX - 1, dotY - 1, dotSize + 2, dotSize + 2);

            System.Drawing.Color dotColor = obsConnected ? System.Drawing.Color.FromArgb(62, 207, 142) : System.Drawing.Color.FromArgb(255, 91, 82);
            using var dotBrush = new System.Drawing.SolidBrush(dotColor);
            g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);

            IntPtr newHIcon = bmp.GetHicon();
            _nid.hIcon = newHIcon;
            _nid.szTip = obsConnected ? "Backtrack (OBS Connected)" : "Backtrack (OBS Disconnected)";

            if (!_added)
            {
                _added = Shell_NotifyIcon(NIM_ADD, ref _nid);
            }
            else
            {
                Shell_NotifyIcon(NIM_MODIFY, ref _nid);
            }

            if (_currentHIcon != IntPtr.Zero)
            {
                DestroyIcon(_currentHIcon);
            }
            _currentHIcon = newHIcon;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update tray icon: {ex.Message}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int lp = lParam.ToInt32();
            if (lp == WM_LBUTTONUP || lp == WM_LBUTTONDBLCLK)
            {
                OnOpenHudRequested?.Invoke();
                handled = true;
            }
            else if (lp == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        SetForegroundWindow(_hwnd);

        Style? contextMenuStyle = _owner.TryFindResource("DarkContextMenu") as Style;
        Style? menuItemStyle = _owner.TryFindResource("DarkMenuItem") as Style;
        Style? separatorStyle = _owner.TryFindResource("DarkSeparator") as Style;

        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = contextMenuStyle
        };

        // OBS Status Header Item
        var obsHeader = new System.Windows.Controls.MenuItem
        {
            Header = _obsConnected ? "OBS Connected" : "OBS Disconnected",
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Style = menuItemStyle
        };
        menu.Items.Add(obsHeader);
        menu.Items.Add(new System.Windows.Controls.Separator { Style = separatorStyle });

        // Open Backtrack HUD
        var openHudItem = new System.Windows.Controls.MenuItem { Header = "Open Backtrack HUD", Style = menuItemStyle };
        openHudItem.Click += (_, _) => OnOpenHudRequested?.Invoke();
        menu.Items.Add(openHudItem);

        // Toggle Status Overlay
        var toggleOverlayItem = new System.Windows.Controls.MenuItem
        {
            Header = _statusOverlayVisible ? "Hide Status Overlay" : "Show Status Overlay",
            Style = menuItemStyle
        };
        toggleOverlayItem.Click += (_, _) => OnToggleStatusOverlayRequested?.Invoke();
        menu.Items.Add(toggleOverlayItem);

        // Open Clips Folder
        var openClipsItem = new System.Windows.Controls.MenuItem { Header = "Open Clips Folder", Style = menuItemStyle };
        openClipsItem.Click += (_, _) => OnOpenClipsFolderRequested?.Invoke();
        menu.Items.Add(openClipsItem);

        // Settings
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings...", Style = menuItemStyle };
        settingsItem.Click += (_, _) => OnOpenSettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator { Style = separatorStyle });

        // Quit
        var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit Backtrack", Style = menuItemStyle };
        quitItem.Click += (_, _) => OnQuitRequested?.Invoke();
        menu.Items.Add(quitItem);

        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }
        if (_currentHIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }
        _baseBitmap?.Dispose();
    }
}
