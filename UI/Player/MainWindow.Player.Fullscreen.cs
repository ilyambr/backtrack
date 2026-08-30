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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private DispatcherTimer? _fullscreenControlsHideTimer;
    private DispatcherTimer? _fullscreenCursorPollTimer;
    private bool _fullscreenControlsHidden;
    private Win32Point _lastGlobalCursorPos;

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

        _fullscreenControlsHidden = false;
        PlayerTitleBarHost.BeginAnimation(UIElement.OpacityProperty, null);
        PlayerTitleBarHost.Opacity = 1;
        PlayerTitleBarHost.IsHitTestVisible = true;
        PlayerFullscreenTransportBorder.BeginAnimation(UIElement.OpacityProperty, null);
        PlayerFullscreenTransportBorder.Opacity = 1;
        PlayerFullscreenTransportBorder.IsHitTestVisible = true;
        Mouse.OverrideCursor = null;

        ReopenPlayerOverlayPopup();
        ReopenPlayerFullscreenTransportPopup();
        UpdateLayout();

        _fullscreenControlsHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _fullscreenControlsHideTimer.Interval = TimeSpan.FromSeconds(3);
        _fullscreenControlsHideTimer.Tick -= FullscreenControlsHideTimer_Tick;
        _fullscreenControlsHideTimer.Tick += FullscreenControlsHideTimer_Tick;
        _fullscreenControlsHideTimer.Stop();
        _fullscreenControlsHideTimer.Start();

        StartFullscreenCursorMonitor();
    }

    private void ExitPlayerFullscreen()
    {
        _isPlayerFullscreen = false;

        _fullscreenControlsHideTimer?.Stop();
        StopFullscreenCursorMonitor();
        _fullscreenControlsHidden = false;
        Mouse.OverrideCursor = null;

        RootBorder.BorderThickness = new Thickness(1);

        PlayerTitleBarHost.BeginAnimation(UIElement.OpacityProperty, null);
        PlayerTitleBarHost.Opacity = 1;
        PlayerTitleBarHost.IsHitTestVisible = true;
        PlayerFullscreenTransportBorder.BeginAnimation(UIElement.OpacityProperty, null);
        PlayerFullscreenTransportBorder.Opacity = 1;
        PlayerFullscreenTransportBorder.IsHitTestVisible = true;

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
        double expectedHeight = contentHeight + 90;
        double targetTop = screenBounds.Y + Math.Max((screenBounds.Height - expectedHeight) / 2, 60);
        SetWindowBoundsSafe(_preFullscreenLeft, targetTop, _preFullscreenWidth);

        PlayerTitlePill.Margin = new Thickness(0);
        PlayerMenuPill.Margin = new Thickness(0, 0, 20, 0);
        PlayerTitleBarHost.Height = 46;
        _scrim.SetExitButtonVisible(true);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
        PlayerFullscreenButton.ToolTip = "Fullscreen";
        ReopenPlayerOverlayPopup();
        UpdateLayout();
    }

    private void StartFullscreenCursorMonitor()
    {
        GetCursorPos(out _lastGlobalCursorPos);
        _fullscreenCursorPollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _fullscreenCursorPollTimer.Tick -= FullscreenCursorPollTimer_Tick;
        _fullscreenCursorPollTimer.Tick += FullscreenCursorPollTimer_Tick;
        _fullscreenCursorPollTimer.Start();
    }

    private void StopFullscreenCursorMonitor()
    {
        _fullscreenCursorPollTimer?.Stop();
    }

    private void FullscreenCursorPollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlayerFullscreen)
        {
            StopFullscreenCursorMonitor();
            return;
        }

        if (GetCursorPos(out Win32Point currentPos))
        {
            if (Math.Abs(currentPos.X - _lastGlobalCursorPos.X) > 2 || Math.Abs(currentPos.Y - _lastGlobalCursorPos.Y) > 2)
            {
                _lastGlobalCursorPos = currentPos;
                NotifyFullscreenActivity();
            }
        }
    }

    private void FullscreenControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlayerFullscreen)
        {
            _fullscreenControlsHideTimer?.Stop();
            return;
        }

        if (PlayerMenuPopup.IsOpen || BookmarkPopup.IsOpen || CompressPopup.IsOpen ||
            PlayerVolumePopup.IsOpen || PlayerFreezeFramePopup.IsOpen ||
            TrimHandleTooltipPopup.IsOpen || _isScrubbing || _isPlayerRenaming || _isTrimming)
        {
            return;
        }

        HideFullscreenControls();
    }

    private void HideFullscreenControls()
    {
        if (!_isPlayerFullscreen || _fullscreenControlsHidden) return;
        _fullscreenControlsHidden = true;

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        PlayerTitleBarHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        PlayerFullscreenTransportBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        PlayerTitleBarHost.IsHitTestVisible = false;
        PlayerFullscreenTransportBorder.IsHitTestVisible = false;

        Mouse.OverrideCursor = Cursors.None;
    }

    private void ShowFullscreenControls()
    {
        if (!_isPlayerFullscreen) return;

        Mouse.OverrideCursor = null;

        if (_fullscreenControlsHidden)
        {
            _fullscreenControlsHidden = false;

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            PlayerTitleBarHost.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            PlayerFullscreenTransportBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            PlayerTitleBarHost.IsHitTestVisible = true;
            PlayerFullscreenTransportBorder.IsHitTestVisible = true;
        }

        _fullscreenControlsHideTimer?.Stop();
        _fullscreenControlsHideTimer?.Start();
    }

    internal void NotifyFullscreenActivity()
    {
        if (!_isPlayerFullscreen) return;
        ShowFullscreenControls();
    }

    private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPlayerFullscreen)
        {
            NotifyFullscreenActivity();
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isPlayerFullscreen)
        {
            NotifyFullscreenActivity();
        }
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
