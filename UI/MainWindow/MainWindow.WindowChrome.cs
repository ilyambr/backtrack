using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using System.Runtime.InteropServices;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(MainWindow_WndProc);
        }
    }

    private IntPtr MainWindow_WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const uint SWP_NOCOPYBITS = 0x0100;

        if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOCOPYBITS;
            Marshal.StructureToPtr(pos, lParam, true);
        }
        return IntPtr.Zero;
    }

    private void PositionInBottomRightCorner()
    {
        const double margin = 20;
        Rect bounds = TargetScreenBounds;
        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        _recentClipsOverlay.Left = bounds.X + bounds.Width - width - margin;
        _recentClipsOverlay.Top = bounds.Y + bounds.Height - height - margin;
    }

    private void SetWindowBoundsSafe(double targetLeft, double targetTop, double targetWidth)
    {
        if (targetWidth < Width)
        {
            Width = targetWidth;
            Left = targetLeft;
            Top = targetTop;
        }
        else
        {
            Left = targetLeft;
            Top = targetTop;
            Width = targetWidth;
        }
    }

    private void CheckDragExitThreshold(Point screenPt)
    {
        if (!IsVisible || _galleryIsRemote) return;

        const double ForcefieldThreshold = 45.0;

        Rect mainRect = new Rect(Left, Top, ActualWidth, ActualHeight);
        mainRect.Inflate(ForcefieldThreshold, ForcefieldThreshold);

        if (mainRect.Contains(screenPt))
            return;

        if (_recentClipsOverlay.IsVisible)
        {
            Rect recentRect = new Rect(_recentClipsOverlay.Left, _recentClipsOverlay.Top, _recentClipsOverlay.ActualWidth, _recentClipsOverlay.ActualHeight);
            recentRect.Inflate(ForcefieldThreshold, ForcefieldThreshold);
            if (recentRect.Contains(screenPt))
                return;
        }

        Dispatcher.BeginInvoke(() => CloseOverlay());
    }

    private void CloseOverlay()
    {
        if (_capturingHotkey)
            EndHotkeyCapture(cancelled: true);
        if (_capturingCancelRecordHotkey)
            EndCancelRecordHotkeyCapture(cancelled: true);
        if (_capturingBookmarkHotkey)
            EndBookmarkHotkeyCapture(cancelled: true);

        StopSettingsAutoscroll();
        ShellDragHelper.ResetDropHelper();

        PlayerOverlayPopup.IsOpen = false;
        PlayerMenuPopup.IsOpen = false;
        PlayerFreezeFramePopup.IsOpen = false;
        PlayerVolumePopup.IsOpen = false;
        PlayerActionFeedbackPopup.IsOpen = false;

        if (PlayerPanel.Visibility == Visibility.Visible)
            DisposeVlcPlayerAsync();

        _lastScreen = Screen.Idle;
        _currentPlayerFile = null;
        _currentPlayerRemoteOrigin = null;
        GalleryGrid.Children.Clear();
        GalleryGrid.Visibility = Visibility.Hidden;
        GalleryScrollHost.Visibility = Visibility.Hidden;
        GalleryPanel.Opacity = 0;

        if (!_settings.EnableAnimations)
        {
            Hide();
            _scrim.Hide();
            ShowScreen(Screen.Idle, skipEntranceAnimation: true);
            UpdateLayout();
        }
        else
        {
            FadeWindowOut(this, durationMs: 80, onCompleted: () =>
            {
                ShowScreen(Screen.Idle, skipEntranceAnimation: true);
                UpdateLayout();
            });
            FadeWindowOut(_scrim);
        }

        _disclaimer.Hide();
        _logo.Hide();
        _streamingStatus.Hide();
        _recentClipsOverlay.Hide();
        _toastOverlay.UpdatePosition(false);
        _updatePrompt.HidePrompt();
        RefreshOverlayLogVisibilityAndMode();

        _statusOverlay.IsHudOpen = false;
        _statusOverlay.Reposition();
    }

    internal void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible)
            return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_activeConfirmDialog != null && _activeConfirmDialog.IsLoaded)
            {
                _activeConfirmDialog.Close();
                _activeConfirmDialog = null;
            }
            else if (TrimPanel.Visibility == Visibility.Visible)
            {

                TrimCancel_Click(sender, e);
            }
            else if (_isPlayerFullscreen)
            {

                ExitPlayerFullscreen();
            }
            else if (_selectedClipPaths.Count > 0)
            {
                _selectedClipPaths.Clear();
                RefreshGallerySelectionUi();
            }
            else
            {
                CloseOverlay();
            }
            return;
        }

        HandlePlayerKeyboardShortcut(e);
    }
}
