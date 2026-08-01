using System;
using System.Windows;
using System.Windows.Interop;
using CaptureCenter.Interop;

namespace CaptureCenter;

public partial class LogoOverlay : Window
{
    public LogoOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 20;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }
}
