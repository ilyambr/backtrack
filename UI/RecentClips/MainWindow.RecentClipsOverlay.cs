using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Updates;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private readonly RecentClipsOverlay _recentClipsOverlay;

    private void InitializeRecentClipsOverlay()
    {
        _recentClipsOverlay.PositionChanged += (x, y) =>
        {
            _settings.RecentClipsOverlayX = x;
            _settings.RecentClipsOverlayY = y;
            _settings.Save();
        };
    }

    private void PositionRecentClipsOverlay()
    {
        if (_settings.RecentClipsOverlayX is double x && _settings.RecentClipsOverlayY is double y)
        {
            _recentClipsOverlay.Left = x;
            _recentClipsOverlay.Top = y;
            return;
        }

        PositionInBottomRightCorner();
        void Handler(object? s, SizeChangedEventArgs e)
        {
            _recentClipsOverlay.SizeChanged -= Handler;
            PositionInBottomRightCorner();
        }
        _recentClipsOverlay.SizeChanged += Handler;
    }

    private void UpdateRecentClipsOverlayVisibility(Screen currentScreen)
    {
        if (!_settings.ShowRecentClipsOverlay || !IsVisible || currentScreen != Screen.Idle)
        {
            _recentClipsOverlay.Hide();
            return;
        }

        RefreshRecentClipsOverlay();
        PositionRecentClipsOverlay();
        _recentClipsOverlay.Show();
    }

    private void RefreshRecentClipsOverlay()
    {
        if (!_settings.ShowRecentClipsOverlay)
            return;

        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _ = RefreshRecentClipsOverlayRemoteAsync();
            return;
        }

        try
        {
            if (!Directory.Exists(_settings.ClipsFolder))
                return;

            List<FileInfo> recent = Directory.EnumerateFiles(_settings.ClipsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                            && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(4)
                .ToList();

            _recentClipsOverlay.SetTiles(recent.Select(BuildRecentClipTile));
        }
        catch
        {

        }
    }

    private async Task RefreshRecentClipsOverlayRemoteAsync()
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        if (all is null)
            return;

        List<(string RelativePath, RemoteGalleryFile File)> recent = all
            .Where(t => !_pendingRemoteDeletePaths.Contains(t.RelativePath))
            .OrderByDescending(t => t.File.Modified)
            .Take(4)
            .ToList();

        _recentClipsOverlay.SetTiles(recent.Select(t => BuildRecentRemoteClipTile(t.RelativePath, t.File)));
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            CloseOverlay();
        }
        else
        {
            _lastScreen = Screen.Idle;
            ShowScreen(Screen.Idle, skipEntranceAnimation: true);
            UpdateLayout();

            double targetWidth = CompactWidth;
            Rect targetBounds = TargetScreenBounds;
            double targetLeft = targetBounds.X + (targetBounds.Width - targetWidth) / 2;
            double targetTop = targetBounds.Y + CompactTop;

            Left = -32000;
            Top = -32000;

            _scrim.ArmDismissCooldown(400);
            _scrim.Show();
            _logo.ShowWithIntro();
            Opacity = 1;
            Show();

            int framesRendered = 0;
            EventHandler? renderHandler = null;
            renderHandler = (s, e) =>
            {
                framesRendered++;
                if (framesRendered >= 2)
                {
                    CompositionTarget.Rendering -= renderHandler;
                    Left = targetLeft;
                    Top = targetTop;
                }
            };
            CompositionTarget.Rendering += renderHandler;
            Activate();

            _statusOverlay.IsHudOpen = true;
            _statusOverlay.Reposition();
            if (_settings.ShowStatusIndicator)
            {
                _statusOverlay.Show();
                WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
            }
            _toastOverlay.Show();
            _toastOverlay.UpdatePosition(true);
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_toastOverlay).Handle);
            RefreshUpdatePromptVisibility();
            RefreshOverlayLogVisibilityAndMode();

            if (_settings.ShowDisclaimer)
                _disclaimer.Show();

            UpdateRecentClipsOverlayVisibility(Screen.Idle);
            UpdateStreamingBoxVisibility();
        }
    }

    internal void DisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplaySelector.SelectedValue is not string deviceName)
            return;

        string? previousDeviceName = _settings.DisplayDeviceName;
        _settings.DisplayDeviceName = deviceName;
        _settings.Save();

        ShowScreen(Screen.Settings);
        _statusOverlay.Reposition();
        _scrim.Reposition();
        _disclaimer.Reposition();
        _logo.Reposition();
        _toastOverlay.UpdatePosition(true);

        if (_settings.ShowRecentClipsOverlay)
        {
            RefreshRecentClipsOverlay();
            RepositionRecentClipsOverlayForDisplayChange(previousDeviceName);
        }
    }

    private void RepositionRecentClipsOverlayForDisplayChange(string? previousDeviceName)
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
        {
            PositionRecentClipsOverlay();
            return;
        }

        Rect oldBounds = DisplayMonitors.ResolveBoundsDiu(previousDeviceName);
        Rect newBounds = TargetScreenBounds;

        double relativeX = oldBounds.Width > 0 ? (x - oldBounds.X) / oldBounds.Width : 0;
        double relativeY = oldBounds.Height > 0 ? (y - oldBounds.Y) / oldBounds.Height : 0;

        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double newX = newBounds.X + relativeX * newBounds.Width;
        double newY = newBounds.Y + relativeY * newBounds.Height;

        double clampedX = Math.Clamp(newX, newBounds.X, Math.Max(newBounds.X, newBounds.X + newBounds.Width - width));
        double clampedY = Math.Clamp(newY, newBounds.Y, Math.Max(newBounds.Y, newBounds.Y + newBounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        _settings.RecentClipsOverlayX = clampedX;
        _settings.RecentClipsOverlayY = clampedY;
        _settings.Save();
    }

    private void RepositionAllForDisplayChange()
    {
        try
        {

            if (IsVisible)
                ShowScreen(_lastScreen, skipEntranceAnimation: true);

            _statusOverlay.Reposition();
            _scrim.Reposition();
            _disclaimer.Reposition();
            _logo.Reposition();
            _toastOverlay.UpdatePosition(true);
            UpdateStreamingBoxVisibility();
            ClampRecentClipsOverlayOnScreen();
        }
        catch (Exception ex)
        {

            AppLog.WriteError("Reposition after display settings changed", ex);
        }
    }

    private void ClampRecentClipsOverlayOnScreen()
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
            return;

        Rect bounds = TargetScreenBounds;
        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double clampedX = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - width));
        double clampedY = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        if (clampedX != x || clampedY != y)
        {
            _settings.RecentClipsOverlayX = clampedX;
            _settings.RecentClipsOverlayY = clampedY;
            _settings.Save();
        }
    }

    internal void ShowRecentClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ShowRecentClipsToggle.IsChecked == true;
        _settings.ShowRecentClipsOverlay = enabled;

        if (!enabled)
        {
            _settings.RecentClipsOverlayX = null;
            _settings.RecentClipsOverlayY = null;
        }
        _settings.Save();

        UpdateRecentClipsOverlayVisibility(_lastScreen);
    }
}
