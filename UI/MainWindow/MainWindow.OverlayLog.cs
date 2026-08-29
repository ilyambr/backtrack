using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly OverlayLogWindow _overlayLog = new();

    private void InitializeOverlayLog()
    {
        AppLog.Write("Backtrack started");
        AppLog.Changed += () => Dispatcher.BeginInvoke(RefreshBacktrackModeLog);

        if (_obsStatsTimer != null)
        {
            _obsStatsTimer.Tick += async (_, _) => await RefreshObsModeLogAsync();
            _obsStatsTimer.Start();
        }
    }

    private void RefreshOverlayLogVisibilityAndMode()
    {
        if (!_settings.OverlayLogEnabled || !IsVisible)
        {
            _overlayLog.Hide();
            return;
        }

        bool obsMode = _settings.OverlayLogMode != "Backtrack";
        _overlayLog.Show();
        _overlayLog.SetMode(obsMode);
        if (obsMode)
            _ = RefreshObsModeLogAsync();
        else
            RefreshBacktrackModeLog();
    }

    private void RefreshBacktrackModeLog()
    {
        if (!_settings.OverlayLogEnabled || !IsVisible || _settings.OverlayLogMode != "Backtrack")
            return;

        List<string> lines = AppLog.Snapshot().Select(e => $"[{e.TimestampLocal:HH:mm:ss}] {e.Message}").ToList();
        _overlayLog.SetBacktrackLines(lines);
    }

    private void ShowObsModeMessage(string text)
    {
        if (!_settings.OverlayLogEnabled || !IsVisible || _settings.OverlayLogMode == "Backtrack")
            return;
        _overlayLog.SetObsLine(text);
        _obsLogClearAtUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private async Task RefreshObsModeLogAsync()
    {
        if (!_obs.IsConnected)
        {
            _overlayLog.SetObsLine("");
            return;
        }

        bool showInOverlayLog = _settings.OverlayLogEnabled && IsVisible && _settings.OverlayLogMode != "Backtrack";

        try
        {
            ObsStats stats = await _obs.GetStatsAsync();
            string? warning = ComputeObsOverloadWarning(stats);
            if (warning is not null)
            {

                _lastEncoderOverloadEventUtc = DateTime.UtcNow;
            }

            if (!showInOverlayLog)
                return;

            if (warning is not null)
            {
                _overlayLog.SetObsLine(warning);
                _obsLogClearAtUtc = null;
                return;
            }

            if (_obsLogClearAtUtc is DateTime clearAt)
            {
                if (DateTime.UtcNow < clearAt)
                    return;
                _obsLogClearAtUtc = null;
            }
            _overlayLog.SetObsLine("");
        }
        catch
        {

        }
    }

    internal void OverlayLogToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayLogEnabled = OverlayLogToggle.IsChecked == true;
        _settings.Save();
        OverlayLogModeFields.Visibility = _settings.OverlayLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshOverlayLogVisibilityAndMode();
    }

    internal void OverlayLogModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.OverlayLogMode = OverlayLogModeSelector.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "Obs";
        _settings.Save();
        RefreshOverlayLogVisibilityAndMode();
    }

}
