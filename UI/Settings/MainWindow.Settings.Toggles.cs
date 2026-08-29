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
    internal void AutoDeleteOldClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoDeleteOldClipsEnabled = AutoDeleteOldClipsToggle.IsChecked == true;
        _settings.Save();
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RestartAutoDeleteOldClipsTimer();
    }

    internal void ExperimentalHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = ExperimentalContent.Visibility != Visibility.Visible;
        ExperimentalContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalHeaderText.Text = expand ? "▾ EXPERIMENTAL" : "▸ EXPERIMENTAL";
    }

    internal void DestructiveHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = DestructiveContent.Visibility != Visibility.Visible;
        DestructiveContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        DestructiveHeaderText.Text = expand ? "▾ MAINTENANCE" : "▸ MAINTENANCE";
    }

    internal void UninstallBacktrackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Uninstall Backtrack? This removes the app, its Start Menu shortcut, and its registry entry. Your clips aren't touched.",
            "Uninstall",
            confirmed =>
            {
                if (!confirmed) return;
                (bool success, string? error) = Backtrack.Interop.SelfUninstall.BeginUninstall();
                if (!success)
                {
                    MessageBox.Show(this, error ?? "Couldn't start the uninstall.", "Backtrack");
                    return;
                }

                Application.Current.Shutdown();
            });
    }

    internal void UninstallSourceRecordButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Uninstall Source Record? OBS will be closed first if it's running.",
            "Uninstall",
            async confirmed =>
            {
                if (!confirmed) return;
                UninstallSourceRecordButton.IsEnabled = false;
                (bool success, string? error) = await _updates.UninstallSourceRecordAsync();
                UninstallSourceRecordButton.IsEnabled = true;
                if (!success)
                    MessageBox.Show(this, error ?? "Couldn't uninstall Source Record.", "Backtrack");
            });
    }

    internal void BufferDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshBufferDurationUi();

    internal async void ApplyBufferDuration_Click(object sender, RoutedEventArgs e)
    {
        int minutes = (int)BufferDurationSlider.Value;
        _settings.ReplayBufferMinutes = minutes;
        _settings.Save();

        try
        {
            await _obs.SetReplayBufferDurationAsync(minutes * 60);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't reach the Replay Slider bridge: {ex.Message}", "Backtrack");
        }
    }

    private ConfirmDialog? _activeConfirmDialog;

    internal void ShowStatusIndicatorToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleStatusOverlay();
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);
    }

    internal void StatusIndicatorOrientationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.StatusIndicatorOrientation = StatusIndicatorOrientationSelector.SelectedItem is ComboBoxItem { Tag: "Vertical" }
            ? StatusIndicatorOrientation.Vertical
            : StatusIndicatorOrientation.Horizontal;
        _settings.Save();
        _statusOverlay.Reposition();
        UpdateStatusIndicatorPreview();
    }

    internal void StatusIndicatorLocationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

        _settings.StatusIndicatorLocation = (StatusIndicatorLocation)StatusIndicatorLocationSelector.SelectedIndex;
        _settings.Save();
        _statusOverlay.Reposition();
        UpdateStatusIndicatorPreview();
    }

    private void UpdateStatusIndicatorPreview()
    {
        bool horizontal = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Horizontal;
        bool isLeft = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.BottomLeft;
        bool isTop = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.TopRight;

        StatusIndicatorPreviewPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        StatusIndicatorPreviewPanel.HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        StatusIndicatorPreviewPanel.VerticalAlignment = isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        Thickness gap = horizontal ? new Thickness(5, 0, 0, 0) : new Thickness(0, 5, 0, 0);
        for (int i = 0; i < StatusIndicatorPreviewPanel.Children.Count; i++)
        {
            if (StatusIndicatorPreviewPanel.Children[i] is FrameworkElement badge)
                badge.Margin = i == 0 ? default : gap;
        }
    }

    internal void StatusIndicatorPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;
        double targetHeight = e.NewSize.Width * 9.0 / 16.0;

        if (double.IsNaN(StatusIndicatorPreviewBorder.Height) || Math.Abs(StatusIndicatorPreviewBorder.Height - targetHeight) > 0.5)
            StatusIndicatorPreviewBorder.Height = targetHeight;
    }

    internal void ShowDisclaimerToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDisclaimer = ShowDisclaimerToggle.IsChecked == true;
        _settings.Save();
        if (!_settings.ShowDisclaimer)
            _disclaimer.Hide();
        else if (IsVisible)
            _disclaimer.Show();
    }

    internal void DisableBacktrackAutoUpdateToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisableBacktrackAutoUpdate = DisableBacktrackAutoUpdateToggle.IsChecked == true;
        _settings.Save();
    }

    internal void DisablePluginAutoUpdateToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisablePluginAutoUpdate = DisablePluginAutoUpdateToggle.IsChecked == true;
        _settings.Save();
    }

    internal void EnableAnimationsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.EnableAnimations = EnableAnimationsToggle.IsChecked == true;
        _settings.Save();
    }

    internal void DiagnosticLogToggle_Click(object sender, RoutedEventArgs e) => SetDiagnosticLogEnabled(DiagnosticLogToggle.IsChecked == true);

    private void SetDiagnosticLogEnabled(bool enabled)
    {
        _settings.DiagnosticLogEnabled = enabled;
        _settings.Save();
        AppLog.FileLoggingEnabled = enabled;
        DiagnosticLogToggle.IsChecked = enabled;
        OpenDiagnosticLogButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
            AppLog.Write("Diagnostic log file enabled");
    }

    internal void DeveloperModeToggle_Click(object sender, RoutedEventArgs e) => SetDeveloperModeEnabled(DeveloperModeToggle.IsChecked == true);

    private void SetDeveloperModeEnabled(bool enabled)
    {
        _settings.DeveloperModeEnabled = enabled;
        _settings.Save();
        AppLog.DeveloperModeEnabled = enabled;
        UpdateService.DeveloperModeEnabled = enabled;
        DeveloperModeToggle.IsChecked = enabled;

        if (enabled && !_settings.DiagnosticLogEnabled)
            SetDiagnosticLogEnabled(true);

        _settings.DisableBacktrackAutoUpdate = enabled;
        _settings.Save();
        DisableBacktrackAutoUpdateToggle.IsChecked = enabled;
        DisableBacktrackAutoUpdateToggle.IsEnabled = !enabled;
    }

    internal void DisableHardwareAccelToggle_Click(object sender, RoutedEventArgs e)
    {

        _settings.DisableHardwareAcceleration = DisableHardwareAccelToggle.IsChecked == true;
        _settings.Save();
        MessageBox.Show(this, "This takes effect the next time Backtrack starts.", "Backtrack");
    }

    internal void OpenDiagnosticLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppLog.LogFilePath))
            {
                MessageBox.Show(this, "Nothing's been logged to the file yet.", "Backtrack");
                return;
            }
            Process.Start(new ProcessStartInfo(AppLog.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the log file: {ex.Message}", "Backtrack");
        }
    }

    internal void LaunchWithWindowsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = LaunchWithWindowsToggle.IsChecked == true;
        try
        {
            if (enabled)
                CreateOrUpdateStartupTask();
            else
                DeleteStartupTask();

            _settings.LaunchWithWindows = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update the startup task: {ex.Message}", "Backtrack");
            LaunchWithWindowsToggle.IsChecked = !enabled;
        }
    }

    private static string SchtasksPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    internal void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
