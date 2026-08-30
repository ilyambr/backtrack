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
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{

    public void MarkFirewallRulesAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _settings.FirewallRulesAttempted = true;
            _settings.Save();
        });
    }

    private void RefreshUpdatePromptVisibility()
    {
        if (IsVisible && _pendingUpdateName is not null && _pendingUpdateInstall is not null)
            _updatePrompt.ShowPrompt(_pendingUpdateName, _pendingUpdateInstall);
        else
            _updatePrompt.HidePrompt();
    }

    private static void PrepareAnimatePanelIn(FrameworkElement panel, bool useCache)
    {

        if (useCache)
            panel.CacheMode = new BitmapCache();
        panel.RenderTransform = new ScaleTransform(0.96, 0.96);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.Opacity = 0;
    }

    private static void StartAnimatePanelIn(FrameworkElement panel)
    {

        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)panel.RenderTransform;

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => panel.CacheMode = null;

        panel.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void ShowScreen(Screen screen, bool skipEntranceAnimation = false)
    {
        _scrim.ArmDismissCooldown(400);

        StopSettingsAutoscroll();

        FrameworkElement newPanel = PanelFor(screen);
        bool switchingPanel = newPanel.Visibility != Visibility.Visible;

        bool animateEntrance = switchingPanel && screen != Screen.Player && !skipEntranceAnimation && _settings.EnableAnimations;

        if (screen != Screen.Player)
        {
            PlayerVideoView.Visibility = Visibility.Collapsed;
            DetachPlayerVideo();
        }

        if (screen != Screen.Gallery)
        {
            GalleryGrid.Children.Clear();
            GalleryGrid.Visibility = Visibility.Hidden;
            GalleryScrollHost.Visibility = Visibility.Hidden;
        }
        else
        {
            GalleryGrid.Visibility = Visibility.Visible;
            GalleryScrollHost.Visibility = Visibility.Visible;
        }

        if (IdlePanel != newPanel) { IdlePanel.Visibility = Visibility.Collapsed; IdlePanel.Opacity = 0; }
        if (SaveReplayPanel != newPanel) { SaveReplayPanel.Visibility = Visibility.Collapsed; SaveReplayPanel.Opacity = 0; }
        if (StartRecordPanel != newPanel) { StartRecordPanel.Visibility = Visibility.Collapsed; StartRecordPanel.Opacity = 0; }
        if (GalleryPanel != newPanel) { GalleryPanel.Visibility = Visibility.Collapsed; GalleryPanel.Opacity = 0; }
        if (PlayerPanel != newPanel) { PlayerPanel.Visibility = Visibility.Collapsed; PlayerPanel.Opacity = 0; }
        if (SettingsPanel != newPanel) { SettingsPanel.Visibility = Visibility.Collapsed; SettingsPanel.Opacity = 0; }

        if (animateEntrance)
            PrepareAnimatePanelIn(newPanel, useCache: screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Settings);
        else if (switchingPanel)
        {
            newPanel.Opacity = 1;
            newPanel.RenderTransform = null;
            newPanel.CacheMode = null;
        }

        newPanel.Visibility = Visibility.Visible;

        bool big = screen is Screen.Gallery or Screen.Player;
        double targetWidth = screen == Screen.Settings ? WideWidth : big ? BigWidth() : CompactWidth;
        Rect targetBounds = TargetScreenBounds;
        double targetLeft = targetBounds.X + (targetBounds.Width - targetWidth) / 2;
        double targetTop;

        if (screen == Screen.Settings)
        {
            double maxScrollHeight = Math.Max(targetBounds.Height - 260, 450);
            SettingsScrollHost.MaxHeight = maxScrollHeight;
            targetTop = targetBounds.Y + Math.Max((targetBounds.Height - (maxScrollHeight + 80)) / 2, 85);
        }
        else if (big)
        {
            double topClearance = 85;
            double bottomClearance = 100;
            double maxAvailHeight = Math.Max(400, targetBounds.Height - topClearance - bottomClearance);
            double maxAvailWidth = targetBounds.Width * 0.90;

            double maxVidH = maxAvailHeight - 90;
            double vidW = Math.Min(maxAvailWidth, maxVidH * 16.0 / 9.0);
            targetWidth = Math.Max(1280, vidW + 2);
            targetLeft = targetBounds.X + (targetBounds.Width - targetWidth) / 2;

            double videoColumnWidth = targetWidth - 2;
            double contentHeight = videoColumnWidth * 9.0 / 16.0;
            double targetPanelHeight = contentHeight + 90;

            targetTop = targetBounds.Y + Math.Max(topClearance, (targetBounds.Height - targetPanelHeight) / 2);

            if (screen == Screen.Player)
            {
                PlayerVideoHost.Height = contentHeight;
            }
            else
            {
                double galleryHeaderAndFooterHeight = 150;
                double maxGalleryHeight = Math.Max(250, targetPanelHeight - galleryHeaderAndFooterHeight);
                GalleryScrollHost.MaxHeight = maxGalleryHeight;

                double innerWidth = targetWidth - 50;
                int cols = Math.Max(3, (int)Math.Round(innerWidth / 232.0));
                double itemW = Math.Floor(innerWidth / cols);
                GalleryGrid.ItemWidth = itemW;
            }
        }
        else
        {
            targetTop = targetBounds.Y + CompactTop;
        }

        SetWindowBoundsSafe(targetLeft, targetTop, targetWidth);

        UpdateLayout();
        UpdateStreamingBoxVisibility();

        if (screen == Screen.Idle)
            _ = RefreshGalleryCountAsync();

        if (animateEntrance)
            StartAnimatePanelIn(newPanel);

        TopRightButtons.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

        if (screen != Screen.Player)
        {
            PlayerOverlayPopup.IsOpen = false;
            PlayerMenuPopup.IsOpen = false;
        }

        if (screen != Screen.Player)
            DisposeVlcPlayerAsync();

        if (screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Gallery or Screen.Settings)
            _lastScreen = screen;

        UpdateRecentClipsOverlayVisibility(screen);

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }
    }

    private double BigWidth()
    {
        return Math.Clamp(TargetScreenBounds.Width * 0.90, 1280, Math.Max(1280, TargetScreenBounds.Width - 40));
    }

    internal void BackToIdle_Click(object? sender = null, RoutedEventArgs? e = null) => ShowScreen(Screen.Idle);

    private void SetRecordIcon(bool active)
    {
        RecordDot.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RecordSquare.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStreamingBoxVisibility()
    {
        if (_isStreaming && IsVisible && IdlePanel.Visibility == Visibility.Visible)
        {
            _streamingStatus.Reposition(new Rect(Left, Top, Width, ActualHeight));
            _streamingStatus.Show();
        }
        else
        {
            _streamingStatus.Hide();
        }
    }
}
