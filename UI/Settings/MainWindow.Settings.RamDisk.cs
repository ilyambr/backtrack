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
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
        private async Task InitializeRamDiskAsync()
    {
        if (!_settings.RamDiskEnabled)
            return;

        (bool ok, string? error) = await Task.Run(EnsureRamDiskReady);
        RefreshRamDiskStatusText();

        if (!ok)
        {
            
            
            
            Debug.WriteLine($"RAM disk setup failed: {error}");
            MessageBox.Show(this, $"Couldn't set up the RAM disk: {error}", "Backtrack");
            return;
        }

        
        
        
        if (!_settings.RamDiskInstructionShown)
        {
            _settings.RamDiskInstructionShown = true;
            _settings.Save();
            MessageBox.Show(this,
                $"RAM disk mounted at {_settings.RamDiskDriveLetter}:\\.\n\n" +
                "One-time step: in OBS, go to Settings > Output > Replay Buffer and set its output path to that drive letter. " +
                "OBS doesn't expose a way for Backtrack to do this part for you automatically.",
                "Backtrack");
        }

        if (_obs.IsConnected)
            _ = PushRamDiskDestDirAsync();
    }


    private (bool Success, string? Error) EnsureRamDiskReady()
    {
        if (!RamDisk.IsDriverInstalled())
        {
            (bool installed, string? installError) = RamDisk.InstallDriverElevated();
            if (!installed)
                return (false, installError);
        }

        (bool ok, string? error) = RamDisk.Mount(_settings.RamDiskDriveLetter, _settings.RamDiskSizeMb);
        AppLog.Write(ok
            ? $"RAM disk mounted at {_settings.RamDiskDriveLetter}: ({_settings.RamDiskSizeMb} MB)"
            : $"RAM disk mount failed: {error}");
        return (ok, error);
    }


        private async Task PushRamDiskDestDirAsync()
    {
        try
        {
            await _obs.SetReplayDestDirAsync(_settings.ClipsFolder);
        }
        catch
        {
            
            
            
        }
    }


    private void RefreshRamDiskStatusText()
    {
        if (!_settings.RamDiskEnabled)
        {
            RamDiskStatusText.Text = "Off";
        }
        else if (!RamDisk.IsDriverInstalled())
        {
            RamDiskStatusText.Text = "Enabled -- driver not installed yet (installs on next apply, needs one admin prompt)";
        }
        else if (RamDisk.IsMounted(_settings.RamDiskDriveLetter))
        {
            RamDiskStatusText.Text = $"Mounted at {_settings.RamDiskDriveLetter}:\\ ({_settings.RamDiskSizeMb} MB)";
        }
        else
        {
            RamDiskStatusText.Text = "Enabled, but not currently mounted";
        }
    }


    private async void RamDiskToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = RamDiskToggle.IsChecked == true;
        await ApplyRamDiskConfigAsync(enabled, _settings.RamDiskDriveLetter, _settings.RamDiskSizeMb);
    }
        private async Task<(bool Success, string? Error)> ApplyRamDiskConfigAsync(bool enabled, char driveLetter, int sizeMb)
    {
        char oldDrive = _settings.RamDiskDriveLetter;
        bool driveOrSizeChanged = oldDrive != driveLetter || sizeMb != _settings.RamDiskSizeMb;

        
        
        
        if ((!enabled || driveOrSizeChanged) && RamDisk.IsMounted(oldDrive))
        {
            await Task.Run(() => RamDisk.Unmount(oldDrive));
            AppLog.Write($"RAM disk unmounted ({oldDrive}:)");
        }

        _settings.RamDiskEnabled = enabled;
        _settings.RamDiskDriveLetter = driveLetter;
        _settings.RamDiskSizeMb = sizeMb;
        _settings.Save();

        Dispatcher.Invoke(() =>
        {
            RamDiskToggle.IsChecked = enabled;
            RamDiskFields.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            RamDiskDriveBox.Text = driveLetter.ToString();
            RamDiskSizeBox.Text = sizeMb.ToString();
        });

        (bool ok, string? error) = enabled ? await Task.Run(EnsureRamDiskReady) : (true, null);

        Dispatcher.Invoke(() =>
        {
            RefreshRamDiskStatusText();
            RefreshBufferDurationUi();
        });

        if (enabled && !ok)
        {
            Debug.WriteLine($"RAM disk setup failed: {error}");
            Dispatcher.Invoke(() => MessageBox.Show(this, $"Couldn't set up the RAM disk: {error}", "Backtrack"));
            return (false, error);
        }

        if (enabled && ok)
        {
            if (!_settings.RamDiskInstructionShown)
            {
                _settings.RamDiskInstructionShown = true;
                _settings.Save();
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    $"RAM disk mounted at {driveLetter}:\\.\n\n" +
                    "One-time step: in OBS, go to Settings > Output > Replay Buffer and set its output path to that drive letter. " +
                    "OBS doesn't expose a way for Backtrack to do this part for you automatically.",
                    "Backtrack"));
            }

            if (_obs.IsConnected)
                _ = PushRamDiskDestDirAsync();
        }

        if (!enabled)
        {
            
            
            
            
            _ = RevertRamDiskDestDirsAsync(oldDrive);

            if (_settings.RamDiskInstructionShown)
            {
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    "RAM disk turned off. Backtrack switched the plugin's clip destination back to your Clips folder.\n\n" +
                    $"One last step in OBS: go to Settings > Output > Replay Buffer and change the output path from {oldDrive}:\\ to a real folder (like your Clips folder). " +
                    "Backtrack can't change this setting automatically, so replay saves won't work until you do.",
                    "Backtrack"));
            }
        }

        return (true, null);
    }


        private void RefreshRamDiskRemoteGating()
    {
        bool remote = _settings.ObsIsRemote;
        LocalRamDiskSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        RemoteRamDiskSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        if (remote)
            _ = LoadRemoteRamDiskUi();
    }


    private async Task LoadRemoteRamDiskUi()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RemoteRamDiskStatusText.Text = "Not paired with a transmitter PC yet -- pair with it first (below, in OBS section).";
            RemoteRamDiskFields.Visibility = Visibility.Collapsed;
            return;
        }

        RemoteRamDiskStatusText.Text = $"Loading from {_settings.PairedPeerName}...";
        RemoteRamDiskFields.Visibility = Visibility.Collapsed;

        RamDiskSnapshot? snapshot = await _pairing.GetRemoteRamDiskSettingsAsync();
        if (snapshot is null)
        {
            RemoteRamDiskStatusText.Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running and re-open Settings to retry.";
            return;
        }

        RemoteRamDiskStatusText.Text = snapshot.Enabled
            ? (snapshot.Mounted
                ? $"Mounted at {snapshot.DriveLetter}:\\ ({snapshot.SizeMb} MB) on {_settings.PairedPeerName}"
                : $"Enabled on {_settings.PairedPeerName}, but not currently mounted")
            : $"Off on {_settings.PairedPeerName}";
        RemoteRamDiskFields.Visibility = Visibility.Visible;
        RemoteRamDiskToggle.IsChecked = snapshot.Enabled;
        RemoteRamDiskDriveBox.Text = snapshot.DriveLetter.ToString();
        RemoteRamDiskSizeBox.Text = snapshot.SizeMb.ToString();
    }


    private async void ApplyRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        await ApplyRamDiskConfigAsync(_settings.RamDiskEnabled, char.ToUpperInvariant(driveText[0]), sizeMb);
    }


    private void SuggestRamDiskSize_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RamDiskTargetMinutesBox.Text.Trim(), out int minutes) || minutes <= 0)
        {
            MessageBox.Show(this, "Enter a number of minutes first.", "Backtrack");
            return;
        }

        ReplayBufferSizing.Estimate? estimate = ReplayBufferSizing.TryEstimate(minutes);
        if (estimate is null)
        {
            MessageBox.Show(this, "Couldn't read OBS's config to estimate this -- enter a size manually.", "Backtrack");
            return;
        }

        RamDiskSizeBox.Text = estimate.Value.SuggestedSizeMb.ToString();
        MessageBox.Show(this,
            $"Suggested {estimate.Value.SuggestedSizeMb} MB for a {minutes}-minute buffer, based on {estimate.Value.Source} (~{estimate.Value.AssumedBitrateKbps} kbps).\n\n" +
            "Click \"Save & apply\" to actually use it.",
            "Backtrack");
    }


    private async void ApplyRemoteRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RemoteRamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RemoteRamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        bool enabled = RemoteRamDiskToggle.IsChecked == true;
        (bool success, string? error) = await _pairing.SetRemoteRamDiskSettingsAsync(enabled, char.ToUpperInvariant(driveText[0]), sizeMb);
        if (!success)
        {
            MessageBox.Show(this, $"Couldn't apply on the transmitter PC: {error}", "Backtrack");
            return;
        }

        await LoadRemoteRamDiskUi();
    }
}
