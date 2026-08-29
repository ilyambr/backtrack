using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void EnterPlayerFullscreen()
    {
        _isPlayerFullscreen = true;
        _preFullscreenWidth = Width;
        _preFullscreenLeft = Left;

        double transportBarHeight = PlayerTransportBar.ActualHeight;

        Rect targetBounds = TargetScreenBounds;
        double videoWidth = targetBounds.Width;
        double videoHeight = videoWidth * 9.0 / 16.0;
        if (videoHeight > targetBounds.Height)
        {
            videoHeight = targetBounds.Height;
            videoWidth = videoHeight * 16.0 / 9.0;
        }

        // Reparent transport bar into popup before resizing the video host
        PlayerVideoColumnDock.Children.Remove(PlayerTransportBar);
        PlayerTransportBar.Background = Brushes.Transparent;
        PlayerFullscreenTransportBorder.Child = PlayerTransportBar;

        const double transportPillSideInset = 40;
        const double transportPillBottomGap = 16;
        const double transportPillVerticalPadding = 12; 
        const double transportPillHorizontalPadding = 40; 
        PlayerFullscreenTransportBorder.Width = videoWidth - transportPillSideInset;
        PlayerTransportBar.Width = PlayerFullscreenTransportBorder.Width - transportPillHorizontalPadding;
        PlayerFullscreenTransportPopup.HorizontalOffset = transportPillSideInset / 2;
        PlayerFullscreenTransportPopup.VerticalOffset =
            videoHeight - (transportBarHeight + transportPillVerticalPadding) - transportPillBottomGap;

        PlayerTitlePill.Margin = new Thickness(10, 10, 0, 0);
        PlayerMenuPill.Margin = new Thickness(0, 10, 30, 0);
        PlayerTitleBarHost.Height = 56;
        _scrim.SetExitButtonVisible(false);
        RootBorder.BorderThickness = new Thickness(0);

        PlayerVideoHost.Height = videoHeight;
        double targetLeft = targetBounds.X + (targetBounds.Width - videoWidth) / 2;
        double targetTop = targetBounds.Y + Math.Max((targetBounds.Height - videoHeight) / 2, 0);
        SetWindowBoundsSafe(targetLeft, targetTop, videoWidth);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenExitIcon);
        PlayerFullscreenButton.ToolTip = "Exit fullscreen";
        ReopenPlayerOverlayPopup();
        ReopenPlayerFullscreenTransportPopup();
        UpdateLayout();
    }


    private void ExitPlayerFullscreen()
    {
        _isPlayerFullscreen = false;

        RootBorder.BorderThickness = new Thickness(1);

        PlayerFullscreenTransportPopup.IsOpen = false;
        PlayerFullscreenTransportBorder.Child = null;
        PlayerTransportBar.ClearValue(BackgroundProperty);
        PlayerTransportBar.ClearValue(WidthProperty);
        DockPanel.SetDock(PlayerTransportBar, Dock.Bottom);
        PlayerVideoColumnDock.Children.Insert(0, PlayerTransportBar);

        double videoColumnWidth = _preFullscreenWidth - 2;
        double contentHeight = Math.Max(videoColumnWidth * 9.0 / 16.0, 320);
        PlayerVideoHost.Height = contentHeight;

        Rect screenBounds = TargetScreenBounds;
        SetWindowBoundsSafe(_preFullscreenLeft, screenBounds.Y + BigTop, _preFullscreenWidth);

        PlayerTitlePill.Margin = new Thickness(0);
        PlayerMenuPill.Margin = new Thickness(0, 0, 20, 0);
        PlayerTitleBarHost.Height = 46;
        _scrim.SetExitButtonVisible(true);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
        PlayerFullscreenButton.ToolTip = "Fullscreen";
        ReopenPlayerOverlayPopup();
        UpdateLayout();
    }


    private void ReopenPlayerOverlayPopup()
    {
        if (!PlayerOverlayPopup.IsOpen)
        {
            PlayerOverlayPopup.IsOpen = true;
        }
        else
        {
            double offset = PlayerOverlayPopup.HorizontalOffset;
            PlayerOverlayPopup.HorizontalOffset = offset + 0.0001;
            PlayerOverlayPopup.HorizontalOffset = offset;
        }

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
    }
}
