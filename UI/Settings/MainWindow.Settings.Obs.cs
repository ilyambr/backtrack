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
    private void RefreshBufferDurationUi()
    {

        if (_settings is null)
            return;

        int minutes = (int)BufferDurationSlider.Value;
        BufferDurationValueText.Text = $"{minutes:00}:00";

        if (!_settings.RamDiskEnabled)
        {
            BufferDurationWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        ReplayBufferSizing.Estimate? estimate = ReplayBufferSizing.TryEstimate(minutes);
        if (estimate is null || estimate.Value.SuggestedSizeMb <= _settings.RamDiskSizeMb)
        {
            BufferDurationWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        BufferDurationWarningText.Text =
            $"⚠ A full flush at {minutes} min is estimated at ~{estimate.Value.SuggestedSizeMb} MB (~{estimate.Value.AssumedBitrateKbps} kbps), " +
            $"more than your {_settings.RamDiskSizeMb} MB RAM disk. Saves at this length risk failing outright -- shorten this or grow the RAM disk first.";
        BufferDurationWarningText.Visibility = Visibility.Visible;
    }

    internal void ApplyObsConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool remote = ObsRemoteToggle.IsChecked == true;
            if (remote && string.IsNullOrWhiteSpace(ObsHostBox.Text))
            {
                MessageBox.Show(this, "Enter the stream PC's address first.", "Backtrack");
                return;
            }

            _settings.ObsIsRemote = remote;
            _settings.ObsHost = ObsHostBox.Text.Trim();
            _settings.ObsPort = int.TryParse(ObsPortBox.Text.Trim(), out int p) ? p : 4455;
            _settings.ObsRemotePassword = ObsPasswordBox.Password;
            _settings.Save();

            BuffersSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            RecordingsSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            if (!remote)
            {
                _ = LoadBufferVisibilityUi();
                _ = LoadRecordFolderUi();
            }

            (string url, string? password, _serverEnabledAtStartup) = ResolveObsConnection();
            _obs.Reconfigure(url, password);
            _ = RefreshStatusAsync();
            _ = RefreshRemoteRowHotkeysAsync();
            RefreshRamDiskRemoteGating();
            RefreshPluginStatusRemoteGating();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't apply that OBS connection: {ex.Message}", "Backtrack");
        }
    }

    private static void CreateOrUpdateStartupTask()
    {

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(SchtasksPath,
            $"/Create /F /SC ONLOGON /RL LIMITED /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to create the startup task."
                : $"schtasks.exe failed to create the startup task: {stderr.Trim()}");
    }

    private static void DeleteStartupTask()
    {

        var psi = new ProcessStartInfo(SchtasksPath, $"/Delete /F /TN \"{ScheduledTaskName}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && !stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to remove the startup task."
                : $"schtasks.exe failed to remove the startup task: {stderr.Trim()}");
    }
}
